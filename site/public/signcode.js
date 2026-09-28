// Regras das placas, iguais as do plugin (plugin/src/Signs): o que o jogador digita, o que o servidor
// grava no lugar e como texto longo passa pelo campo de 50 caracteres do jogo. Sem DOM: o servidor do
// site usa o mesmo leitor de catalogo, e os testes rodam no Node. Mudou la, muda aqui.

export const LIMIT = 50;
export const CONTINUATION = '>>';

const ACCENTED = 'áàâãäåéèêëíìîïóòôõöúùûüçñ';
const PLAIN = 'aaaaaaeeeeiiiiooooouuuucn';

// SignCode.Normalize: minusculas, sem acento, so letras e digitos.
export function normalize(name) {
  let out = '';
  for (const raw of name) {
    let c = raw.toLowerCase();
    const i = ACCENTED.indexOf(c);
    if (i >= 0) c = PLAIN[i];
    if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) out += c;
  }
  return out;
}

const SIZE_AT_END = /<size=([0-9]+(?:\.[0-9]+)?)>\s*$/;
const SIZE_AT_START = /^\s*<size=([0-9]+(?:\.[0-9]+)?)>/;
const PERCENT_AT_END = /^(.*?)\s*([0-9]{1,4})\s*%$/;

// SignCode.TryParse: ":wood:", "Madeira :wood:" ou ":wood: Madeira", com <size=N> colado no codigo (lado
// do icone) e N% no fim dele (brilho). null = nao e pedido de icone.
export function parseIcon(text) {
  if (text == null) return null;
  const trimmed = text.trim();
  if (trimmed.length < 3 || trimmed.includes('\n')) return null;
  let inner, rest, sized, atEnd;
  if (trimmed.endsWith(':')) {
    const open = trimmed.lastIndexOf(':', trimmed.length - 2);
    if (open < 0) return null;
    inner = trimmed.slice(open + 1, -1);
    rest = trimmed.slice(0, open);
    sized = SIZE_AT_END.exec(rest);
    if (sized) rest = rest.slice(0, sized.index);
    atEnd = true;
  } else {
    sized = SIZE_AT_START.exec(trimmed);
    const body = sized ? trimmed.slice(sized[0].length).trimStart() : trimmed;
    if (body.length < 3 || body[0] !== ':') return null;
    const close = body.indexOf(':', 1);
    if (close < 0) return null;
    inner = body.slice(1, close);
    rest = body.slice(close + 1);
    atEnd = false;
  }
  if (/[<>]/.test(inner)) return null;
  let brightness = null;
  const percent = PERCENT_AT_END.exec(inner.trim());
  if (percent && normalize(percent[1]).length > 0) {
    inner = percent[1];
    brightness = Number(percent[2]);
  }
  const key = normalize(inner);
  if (!key) return null;
  rest = rest.trim();
  return {
    key,
    inner: inner.trim(),
    size: sized ? Number(sized[1]) : null,
    brightness,
    label: rest ? { text: rest, shown: true } : { text: inner.trim(), shown: false },
    atEnd,
  };
}

// O caminho de volta: monta o codigo a partir das partes (o que os controles da pagina mexem).
export function buildIcon({ inner, size, brightness, label, atEnd }) {
  const code = (size != null ? `<size=${size}>` : '') + ':' + inner + (brightness != null ? ` ${brightness}%` : '') + ':';
  if (!label || !label.shown) return code;
  return atEnd ? `${label.text} ${code}` : `${code} ${label.text}`;
}

// ---- catalogo ----

// SignIconCatalog.Parse, mais o que o site quer saber para a galeria: de que arquivo e de que grupo
// cada desenho veio. Linhas `#@ chave valor` sao do site (o plugin ignora comentario):
//   #@ grupo <titulo>        comeca um grupo de artes (sem ele, vale o texto do comentario de cabecalho)
//   #@ dica <texto>          uma linha de ajuda do grupo
//   #@ nome <id> <nome>      como a arte aparece e o codigo que o clique escreve
//   #@ vitrine <id> <id> ... o que vira miniatura no grupo; o resto so pelo nome (sem ela, tudo)
export function parseCatalog(files) {
  const texts = new Map();
  const titled = new Map();
  const aliasList = [];
  const macros = {};
  const params = {};
  const groups = [];
  const origin = new Map();
  const names = new Map();
  let defaultIcon = null;
  for (const { lines, kind } of files) {
    let group = null;
    const open = (title) => {
      group = { title, hint: '', ids: [], shown: null };
      groups.push(group);
    };
    for (const line of lines) {
      if (!line) continue;
      if (line[0] === '#') {
        if (kind !== 'custom') continue;
        const meta = /^#@\s*(\S+)\s*(.*)$/.exec(line);
        if (!meta) {
          const title = line.replace(/^#+\s*/, '').trim();
          if (title) open(title);
          continue;
        }
        const [, key, value] = meta;
        if (key === 'grupo') {
          if (group && !group.ids.length && !group.named) group.title = value.trim();
          else open(value.trim());
          group.named = true;
        } else if (key === 'dica') {
          if (!group) open('');
          group.hint = group.hint ? `${group.hint}\n${value.trim()}` : value.trim();
        } else if (key === 'nome') {
          const m = /^(\S+)\s+(.+)$/.exec(value.trim());
          if (m) names.set(m[1], m[2].trim());
        } else if (key === 'vitrine') {
          if (!group) open('');
          group.shown = (group.shown || []).concat(value.trim().split(/\s+/).filter(Boolean));
        }
        continue;
      }
      const f = line.split('\t');
      if (f[0] === 'I' && f.length === 3 && f[1] && f[2]) {
        texts.set(f[1], f[2].replaceAll('\\n', '\n'));
        if (kind === 'custom') {
          if (!group) open('');
          group.ids.push(f[1]);
        }
        origin.set(f[1], kind);
      } else if (f[0] === 'T' && f.length === 3 && f[1] && f[2]) titled.set(f[1], f[2].replaceAll('\\n', '\n'));
      else if (f[0] === 'A' && f.length === 3) aliasList.push([f[1], f[2]]);
      else if (f[0] === 'D' && f.length === 2) defaultIcon = f[1];
      else if (f[0] === 'P' && f.length === 3 && Number.isFinite(Number(f[2]))) params[f[1]] = Number(f[2]);
      else if (f[0] === 'M' && f.length === 3) {
        const name = f[1].toLowerCase();
        if (/^[a-z0-9]+$/.test(name) && f[2]) macros[name] = f[2].replaceAll('\\n', '\n');
      }
    }
  }
  const aliases = {};
  for (const [alias, target] of aliasList) {
    if (texts.has(alias)) continue;
    if (texts.has(target)) aliases[alias] = target;
  }
  // Um id redefinido no custom.txt sai do grupo de itens e fica so no grupo da arte.
  for (const g of groups) g.ids = g.ids.filter((id) => origin.get(id) === 'custom');
  return {
    texts, titled, aliases, macros, params, names,
    defaultIcon: defaultIcon && texts.has(defaultIcon) ? defaultIcon : null,
    groups: groups.filter((g) => g.ids.length),
    items: [...texts.keys()].filter((id) => origin.get(id) === 'catalog'),
  };
}

// ---- Compose: o texto que o servidor grava no lugar do codigo ----

const LARGE_LABEL = 10;
const LONGEST_SHOWN_LABEL = 22;
const LARGEST_SIZE = 18;
const TAG = /<[^<>]*>/g;
const LABEL_TAG = /^<\/?(?:#|b>|i>|u>|s>|color\b|alpha\b|material\b|mark\b)/i;
const HEADER = /<cspace=-([0-9.]+)>(<material=[^<>]*>)?<line-height=([0-9.]+)><size=([0-9.]+)>/;
const BLOCKS = /^(?:█|\n|<#[0-9a-fA-F]{3,8}>)*$/;
const COLOR_TAG = /<#([0-9a-fA-F]{3,8})>/g;
const HOVER_RESET = '<size=100.0%><cspace=0.0><line-height=100.0%>';

// Sempre com ponto: e o que faz a tag sobreviver no hover do jogo ("0.0##" do .NET).
function format(value) {
  const s = (Math.round(value * 1000) / 1000).toFixed(3).replace(/0+$/, '');
  return s.endsWith('.') ? `${s}0` : s;
}

const param = (params, name, fallback) => (params && Number.isFinite(params[name]) ? params[name] : fallback);

function resize(template, plain, size, params) {
  const reference = HEADER.exec(plain);
  const header = HEADER.exec(template);
  if (!reference || !header) return template;
  let drawing = template.slice(header.index + header[0].length);
  if (drawing.endsWith(HOVER_RESET)) drawing = drawing.slice(0, -HOVER_RESET.length);
  if (!BLOCKS.test(drawing)) return template;
  const units = param(params, 'units', 7.6);
  size = Math.max(1, Math.min(LARGEST_SIZE, size));
  const pixel = Number(reference[3]) * size / units;
  const fitting = Number(header[3]);
  const overlap = Number(header[4]) / fitting - 1;
  const bold = pixel <= fitting + 1e-6 ? param(params, 'bold_fit', 0.24) : param(params, 'bold_overflow', 0.03);
  const resized = `<cspace=-${format(bold + overlap * pixel)}>${header[2] || ''}<line-height=${format(pixel)}><size=${format(pixel * (1 + overlap))}>`;
  return template.slice(0, header.index) + resized + template.slice(header.index + header[0].length);
}

function scale(hex, factor) {
  const n = hex.length;
  if (n !== 3 && n !== 4 && n !== 6 && n !== 8) return `<#${hex}>`;
  const width = n <= 4 ? 1 : 2;
  const full = width === 1 ? 15 : 255;
  let out = '<#';
  for (let channel = 0; channel * width < n; channel++) {
    let digits = hex.substr(channel * width, width);
    if (channel < 3) {
      const scaled = Math.floor(parseInt(digits, 16) * factor + 0.5);
      digits = Math.min(full, scaled).toString(16).padStart(width, '0');
    }
    out += digits;
  }
  return `${out}>`;
}

function dim(template, percent, params) {
  const standard = param(params, 'brightness', 1);
  const factor = Math.min(1, standard * Math.max(5, percent ?? 100) / 100);
  if (Math.abs(factor - 1) < 1e-9) return template;
  const label = template.indexOf('{label}');
  const start = label < 0 ? 0 : template.indexOf('\n', label) + 1;
  return template.slice(0, start) + template.slice(start).replace(COLOR_TAG, (m, hex) => scale(hex, factor));
}

// entry = { i: desenho sem rotulo, t: desenho com rotulo a mostra (ou null) }.
export function compose(entry, label, size, brightness, { params, macros } = {}) {
  const plain = entry?.i;
  if (!plain) return null;
  const text = expand(label.text || '', macros).replace(TAG, (m) => (LABEL_TAG.test(m) ? m : ''));
  const visible = text.replace(TAG, '').length;
  let template = label.shown && visible <= LONGEST_SHOWN_LABEL && entry.t ? entry.t : plain;
  if (size != null) template = resize(template, plain, size, params);
  template = dim(template, brightness, params);
  return template.replaceAll('{ls}', visible <= LARGE_LABEL ? '2' : '1').replaceAll('{label}', text);
}

// Como o rotulo sai: grande, pequeno ou so no hover (o que o Compose escolhe).
export function labelMode(label, macros) {
  if (!label.shown) return 'hidden';
  const visible = expand(label.text, macros).replace(TAG, (m) => (LABEL_TAG.test(m) ? m : '')).replace(TAG, '').length;
  return visible <= LARGE_LABEL ? 'large' : visible <= LONGEST_SHOWN_LABEL ? 'small' : 'hover';
}

// SignIconCatalog.Expand: cada {abreviacao} conhecida vira o texto dela, numa passada so.
export function expand(text, macros) {
  if (!text || !macros || !text.includes('{')) return text;
  let out = '';
  let i = 0;
  while (i < text.length) {
    const close = text[i] === '{' ? text.indexOf('}', i + 1) : -1;
    const expansion = close > i ? macros[text.slice(i + 1, close).toLowerCase()] : undefined;
    if (expansion !== undefined) {
      out += expansion;
      i = close + 1;
    } else {
      out += text[i];
      i++;
    }
  }
  return out;
}

export const hasMacro = (text, macros) => !!text && text.includes('{') && expand(text, macros) !== text;

// ---- do editor ao jogo ----

// O campo do jogo e de uma linha: Enter vira o \n escrito, que o TextMeshPro entende.
export const flat = (text) => text.replace(/\r?\n/g, '\\n');

// Trocas que nao mudam nada na placa e economizam caracteres: <color=#..> e <#aabbcc> viram <#abc>
// quando a cor cabe em 12 bits, e o texto de uma abreviacao volta a ser {abreviacao}. So a tag curta
// <#..> aceita 3 digitos (mark e color= com 3 saem brancos).
export function compact(text, macros) {
  let out = text.replace(/<color=(#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)>/g, '<$1>');
  out = out.replace(/<#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})>/g, (m, hex) => {
    const pairs = hex.match(/../g);
    return pairs.every((p) => p[0].toLowerCase() === p[1].toLowerCase()) ? `<#${pairs.map((p) => p[0]).join('')}>` : m;
  });
  for (const [name, expansion] of Object.entries(macros || {}).sort((a, b) => b[1].length - a[1].length)) {
    if (expansion.length > name.length + 2) out = out.split(expansion).join(`{${name}}`);
  }
  return out;
}

// Onde cortar: nunca no meio de um emoji nem de um \n escrito, nem encostado em espaco (o campo pode
// aparar), e sem que o pedaco pareca pedido de icone (o servidor desenharia em vez de emendar).
function cut(text, start, room, prefix) {
  let end = Math.min(text.length, start + room);
  const min = start + Math.max(1, Math.floor(room / 2));
  // Tag ou {abreviacao} partida ao meio funciona (o servidor junta antes de ler), mas ate a ultima colada
  // a placa mostraria o pedaco escrito; se der, corta antes dela.
  for (const [open, close] of [['<', '>'], ['{', '}']]) {
    const at = text.lastIndexOf(open, end - 1);
    if (end < text.length && at >= min && text.lastIndexOf(close, end - 1) < at && text.indexOf(close, at) >= end) end = at;
  }
  while (end < text.length && end > min) {
    const last = text.charCodeAt(end - 1);
    const next = text[end];
    if ((last >= 0xd800 && last <= 0xdbff) || text[end - 1] === '\\' || text[end - 1] === ' ' || next === ' ' || next === '>') {
      end--;
      continue;
    }
    break;
  }
  while (end > start + 1 && parseIcon(prefix + text.slice(start, end))) end--;
  return end;
}

// Texto (ja no formato do campo) -> o que colar, em ordem. O 1o pedaco e texto comum; os outros comecam
// com >> e o servidor emenda na placa.
export function slice(text) {
  if (text.length <= LIMIT) return text ? [text] : [];
  const parts = [];
  let i = 0;
  while (i < text.length) {
    const prefix = parts.length ? CONTINUATION : '';
    const end = cut(text, i, LIMIT - prefix.length, prefix);
    parts.push(prefix + text.slice(i, end));
    i = end;
  }
  return parts;
}

// O plano inteiro para um texto do editor: o que colar, o que a placa mostra no fim e o que avisar.
// catalog: { known(key), resolve(key), entry(id) -> {i,t} | undefined, params, macros }.
export function plan(source, catalog) {
  const macros = catalog?.macros || {};
  const typed = compact(flat(source), macros);
  const parts = slice(typed);
  const notes = [];
  const icon = parts.length === 1 ? parseIcon(typed) : null;
  if (icon && catalog) {
    const id = catalog.resolve(icon.key);
    if (!id) return { kind: 'text', typed, parts, final: typed, notes };
    const entry = catalog.entry(id);
    return {
      kind: 'icon', typed, parts, icon, id, known: catalog.known(icon.key), mode: labelMode(icon.label, macros),
      final: entry ? compose(entry, icon.label, icon.size, icon.brightness, catalog) : null, notes,
    };
  }
  if (!icon && parts.length > 1 && parseIcon(typed)) notes.push('icon-too-long');
  if (typed.startsWith(CONTINUATION)) notes.push('starts-with-continuation');
  // SignTextRules: cada >> (quantos vierem) emenda no que ja foi escrito; o resto e texto novo.
  let joined = '';
  let written = false;
  for (const part of parts) {
    let rest = part;
    while (rest.startsWith(CONTINUATION)) rest = rest.slice(CONTINUATION.length);
    written ||= rest !== part || hasMacro(part, macros);
    joined = rest !== part ? joined + rest : part;
  }
  const final = written ? expand(joined, macros) : typed;
  return { kind: parts.length > 1 ? 'long' : hasMacro(typed, macros) ? 'macro' : 'text', typed, parts, final, notes };
}

// O hover do jogo mostra o texto sem as tags; tag com ponto no numero ele nao reconhece e deixa passar,
// e o desenho do icone vira cisco. Aqui fica so o que se le.
export function hoverText(final) {
  return (final || '')
    .replace(/\\n/g, ' ')
    .replace(/<[^<>]*>/g, '')
    .replace(/[▀-▟]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}
