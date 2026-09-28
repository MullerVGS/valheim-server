// Simulacao da placa do Valheim: o TextMeshPro do jogo reescrito em JS (parser + layout) e uma cena
// que imita o que se ve la: tabua escura, material iluminado cortando a cor pela metade, e o que e
// sem iluminacao ({u}, icones, rotulo) brilhando com bloom.
// Regras e numeros lidos do cliente 1.0.14 (prefab `sign`, Unity.TextMeshPro.dll, font assets):
// area de texto 18,29 x 8,55 unidades, tabua 20 x 10 (1 m x 0,5 m), centro/meio, quebra de linha
// ligada, overflow sem recorte, Bold, tamanho automatico de 1 a 8 em passos de 0,05.
var RECT_W = 18.29, RECT_H = 8.55, BOARD_W = 20, BOARD_H = 10, SIZE_MIN = 1, SIZE_MAX = 8;
var BASE_COLOR = [30, 26, 18, 255], LINE_HEIGHT_EM = 1.1, KERNING = true, ITALIC_SLANT = 0.35;
var UNLIT = /^valheim_fonts\/valheim-(norse|norsebold|rune|prstartk|averiasans|averiaserif)( - outline)?$/;

// asc/desc em em (faceInfo / pointSize 48). `spacing` = normalSpacingOffset + boldSpacing, que o TMP
// soma ao avanco vezes o tamanho BASE x 0,01. O Norse troca para o typeface Norsebold (sem bold
// simulado, sem extra); o que vem das fontes de fallback ganha (-2 + 5).
var FACES = {
  norse: { font: '700 100px "Sign Norse"', asc: 0.92, desc: -0.18, spacing: 0 },
  latin: { font: '200 100px "Noto Sans", sans-serif', asc: 1.069, desc: -0.293, spacing: 3 },
  block: { font: '300 100px "Noto Sans JP", "Noto Sans", sans-serif', asc: 1.16, desc: -0.288, spacing: 3 },
  emoji: { font: '300 100px "Noto Emoji", sans-serif', asc: 0.9277, desc: -0.2441, spacing: 3 }
};
var NORSE = [32,126,160,163,165,166,168,169,171,177,180,180,182,184,187,187,191,311,313,329,332,382,508,511,536,537,710,711,728,733,1025,1025,1028,1028,1030,1031,1040,1103,1105,1105,1108,1108,1110,1111,1168,1169,5792,5872,7808,7813,7922,7923,8211,8212,8216,8218,8220,8222,8224,8226,8230,8230,8240,8240,8249,8250,8260,8260,8364,8364,8482,8482,8722,8722];
var NAMED = { black: "000000", blue: "0000ff", green: "00ff00", grey: "808080", lightblue: "add8e6", orange: "ff8000",
              purple: "a020f0", red: "ff0000", white: "ffffff", yellow: "ffff00" };
var EMOJI = /\p{Extended_Pictographic}/u;

function inNorse(cp) { for (var i = 0; i < NORSE.length; i += 2) if (cp >= NORSE[i] && cp <= NORSE[i + 1]) return true; return false; }
function faceOf(cp) {
  if (inNorse(cp)) return "norse";
  if ((cp >= 0x2500 && cp <= 0x25FF) || (cp >= 0x2E80 && cp <= 0x9FFF) || (cp >= 0xFF00 && cp <= 0xFFEF)) return "block";
  if (EMOJI.test(String.fromCodePoint(cp))) return "emoji";
  return "latin";
}
function isSpace(cp) { return cp === 32 || cp === 9 || cp === 0xA0 || cp === 0x200B || cp === 0x3000; }

// ---- medidas: a propria fonte do jogo no canvas, em em ----
var mctx = document.createElement("canvas").getContext("2d"), widths = {}, epoch = 0;
function measure(face, str) {
  var key = face + epoch + str, w = widths[key];
  if (w === undefined) { mctx.font = FACES[face].font; w = widths[key] = mctx.measureText(str).width / 100; }
  return w;
}
function advanceOf(face, ch, cp) {
  if (cp === 0x200B) return 0;
  if (face === "block") return 1;                       // Noto Sans JP: blocos, caixas e formas ocupam 1 em
  return measure(face, ch);
}
function kernOf(a, b) { return KERNING ? measure("norse", a + b) - measure("norse", a) - measure("norse", b) : 0; }

// ---- cores ----
function hexColor(hex, strict) {
  var n = hex.length, d = function (i, w) { var v = parseInt(hex.substr(i, w), 16); return w === 1 ? v * 17 : v; };
  if (!/^[0-9a-fA-F]+$/.test(hex)) return null;
  if (!strict && n === 3) return [d(0, 1), d(1, 1), d(2, 1), 255];
  if (!strict && n === 4) return [d(0, 1), d(1, 1), d(2, 1), d(3, 1)];
  if (n === 6) return [d(0, 2), d(2, 2), d(4, 2), 255];
  if (n === 8) return [d(0, 2), d(2, 2), d(4, 2), d(6, 2)];
  return strict ? [255, 255, 255, 255] : null;          // <mark> e color= do sprite: 3 ou 4 digitos saem branco
}
function colorValue(v) {
  if (v.charAt(0) === "#") return hexColor(v.slice(1), false);
  return NAMED[v.toLowerCase()] ? hexColor(NAMED[v.toLowerCase()], false) : null;
}

// ---- parser: texto -> itens com o estilo vigente ----
function num(v) {
  var m = /^([+-]?(?:\d+\.?\d*|\.\d+))(em|%|px)?$/.exec(v);
  return m ? { v: parseFloat(m[1]), u: m[2] === "px" ? "" : (m[2] || ""), signed: /^[+-]/.test(v) } : null;
}
function unquote(v) { return v.replace(/^"(.*)"$/, "$1").replace(/^'(.*)'$/, "$1"); }
function attrs(body) {
  var out = {}, re = /([a-zA-Z-]+)=("[^"]*"|'[^']*'|[^\s]+)/g, m;
  while ((m = re.exec(body))) out[m[1].toLowerCase()] = unquote(m[2]);
  return out;
}

function parse(source, macros) {
  var text = source.replace(/\{([^{}]+)\}/g, function (all, name) { return macros && macros[name] != null ? macros[name] : all; })
                   .replace(/\\n/g, "\n").replace(/\\t/g, "\t");
  var st = { size: null, color: BASE_COLOR, alpha: 255, italic: false, underline: false, strike: false, mark: null, fx: 1, rot: 0,
             voffset: null, cspace: null, mspace: null, lineHeight: null, align: "center", indent: null, marginL: null, marginR: null,
             width: null, nobr: false, unlit: false, outline: false, mult: 1, shift: 0, caseMode: "" };
  var stacks = { size: [], color: [], align: [], material: [], indent: [], width: [] };
  var items = [], unknown = [], noparse = false, i = 0;
  function set(changes) { st = Object.assign({}, st, changes); }

  function tag(body) {
    var lower = body.toLowerCase(), eq = body.indexOf("="), sp = body.search(/\s/);
    var cut = eq < 0 ? (sp < 0 ? body.length : sp) : (sp >= 0 && sp < eq ? sp : eq);
    var name = lower.slice(0, cut), value = eq >= 0 && cut === eq ? unquote(body.slice(eq + 1).split(/\s+(?=[a-zA-Z-]+=)/)[0].trim()) : "";
    var n = num(value), c;
    if (noparse) { if (name === "/noparse") { noparse = false; return true; } return false; }
    if (name.charAt(0) === "#") { c = hexColor(name.slice(1), false); if (!c) return false; stacks.color.push(st.color); set({ color: c }); return true; }
    switch (name) {
      case "b": case "/b": case "strong": case "/strong": return true;      // a placa ja e Bold, e </b> nao desliga
      case "i": case "em": set({ italic: true }); return true;
      case "/i": case "/em": set({ italic: false }); return true;
      case "u": set({ underline: true }); return true;
      case "/u": set({ underline: false }); return true;
      case "s": case "strikethrough": set({ strike: true }); return true;
      case "/s": case "/strikethrough": set({ strike: false }); return true;
      case "color": c = colorValue(value); if (!c) return false; stacks.color.push(st.color); set({ color: c }); return true;
      case "/color": set({ color: stacks.color.length ? stacks.color.pop() : BASE_COLOR }); return true;
      case "alpha": c = /^#([0-9a-fA-F]{2})$/.exec(value); if (!c) return false; set({ alpha: parseInt(c[1], 16) }); return true;
      case "/alpha": set({ alpha: 255 }); return true;
      case "mark":
        c = value.charAt(0) === "#" ? hexColor(value.slice(1), true) : null;
        if (!c) return false;
        c = c.slice(); c[3] = Math.min(c[3], st.color[3], st.alpha);           // alfa do mark = min(cor vigente, mark)
        set({ mark: c }); return true;
      case "/mark": set({ mark: null }); return true;
      case "size":
        if (!n) return false;
        stacks.size.push(st.size);
        set({ size: n.signed && !n.u ? { v: n.v, u: "+" } : n }); return true;
      case "/size": set({ size: stacks.size.length ? stacks.size.pop() : null }); return true;
      case "scale": if (!n) return false; set({ fx: n.v }); return true;
      case "/scale": set({ fx: 1 }); return true;
      case "rotate": if (!n) return false; set({ rot: n.v }); return true;
      case "/rotate": set({ rot: 0 }); return true;
      case "voffset": if (!n || n.u === "%") return false; set({ voffset: n }); return true;
      case "/voffset": set({ voffset: null }); return true;
      case "cspace": if (!n || n.u === "%") return false; set({ cspace: n }); return true;
      case "/cspace": set({ cspace: null }); return true;
      case "mspace": if (!n || n.u === "%") return false; set({ mspace: n }); return true;
      case "/mspace": set({ mspace: null }); return true;
      case "line-height": if (!n) return false; set({ lineHeight: n }); return true;
      case "/line-height": set({ lineHeight: null }); return true;
      case "pos": if (!n) return false; items.push({ k: "pos", n: n, st: st }); return true;
      case "space": if (!n || n.u === "%") return false; items.push({ k: "space", n: n, st: st }); return true;
      case "indent": if (!n) return false; stacks.indent.push(st.indent); set({ indent: n }); return true;
      case "/indent": set({ indent: stacks.indent.length ? stacks.indent.pop() : null }); return true;
      case "line-indent": case "/line-indent": return true;
      case "margin": if (!n) return false; set({ marginL: n, marginR: n }); return true;
      case "margin-left": if (!n) return false; set({ marginL: n }); return true;
      case "margin-right": if (!n) return false; set({ marginR: n }); return true;
      case "/margin": set({ marginL: null, marginR: null }); return true;
      case "width": if (!n) return false; stacks.width.push(st.width); set({ width: n }); return true;
      case "/width": set({ width: stacks.width.length ? stacks.width.pop() : null }); return true;
      case "align":
        if (["left", "center", "right", "justified", "flush"].indexOf(value.toLowerCase()) < 0) return false;
        stacks.align.push(st.align); set({ align: value.toLowerCase() }); return true;
      case "/align": set({ align: stacks.align.length ? stacks.align.pop() : "center" }); return true;
      case "nobr": set({ nobr: true }); return true;
      case "/nobr": set({ nobr: false }); return true;
      case "br": items.push({ k: "nl", st: st }); return true;
      case "zwsp": items.push(glyph("​")); return true;
      case "nbsp": items.push(glyph(" ")); return true;
      case "sup": set({ mult: 0.5, shift: 0.45 }); return true;
      case "sub": set({ mult: 0.5, shift: -0.15 }); return true;
      case "/sup": case "/sub": set({ mult: 1, shift: 0 }); return true;
      case "uppercase": case "allcaps": set({ caseMode: "upper" }); return true;
      case "lowercase": set({ caseMode: "lower" }); return true;
      case "smallcaps": set({ caseMode: "small" }); return true;
      case "/uppercase": case "/allcaps": case "/lowercase": case "/smallcaps": set({ caseMode: "" }); return true;
      case "noparse": noparse = true; return true;
      case "material":
        stacks.material.push([st.unlit, st.outline]);
        set({ unlit: UNLIT.test(value.toLowerCase()), outline: / - outline$/i.test(value) }); return true;
      case "/material": c = stacks.material.pop() || [false, false]; set({ unlit: c[0], outline: c[1] }); return true;
      case "sprite":
        var a = attrs(body), tint = a.color ? (a.color.charAt(0) === "#" ? hexColor(a.color.slice(1), true) : null) : null;
        if (!/^sprite(=|\s|$)/.test(lower)) return false;
        items.push({ k: "sprite", st: st, color: tint || (a.tint === "1" ? st.color : [255, 255, 255, 255]) }); return true;
      // existem e nao mudam nada que de para ver aqui (<font> cai no texto normal, visto no jogo)
      case "font": case "/font": case "font-weight": case "/font-weight": case "style": case "/style": case "link": case "/link":
      case "a": case "/a": case "gradient": case "/gradient": case "page": case "/page": case "cr": case "shy": return true;
    }
    return false;
  }
  function glyph(ch) { var cp = ch.codePointAt(0); return { k: "c", ch: ch, cp: cp, face: faceOf(cp), st: st }; }

  while (i < text.length) {
    var ch = text.charAt(i);
    if (ch === "<") {
      var close = text.indexOf(">", i + 1);
      if (close > i && close - i <= 128 && text.slice(i + 1, close).indexOf("<") < 0) {
        var body = text.slice(i + 1, close);
        if (tag(body)) { i = close + 1; continue; }
        if (/^\/?[a-zA-Z#]/.test(body) && unknown.indexOf("<" + body + ">") < 0) unknown.push("<" + body + ">");
      }
    }
    if (ch === "\n") { items.push({ k: "nl", st: st }); i++; continue; }
    if (ch === "\r") { i++; continue; }
    var cp = text.codePointAt(i), full = String.fromCodePoint(cp);
    i += full.length;
    if (cp === 0xFE0F || cp === 0xFE0E || cp === 0x200D) continue;               // seletores de variacao: sem glifo
    if (st.caseMode === "upper" || st.caseMode === "small") full = full.toUpperCase();
    else if (st.caseMode === "lower") full = full.toLowerCase();
    Array.from(full).forEach(function (c) { items.push(glyph(c)); });
  }
  return { items: items, unknown: unknown };
}

// ---- layout para um tamanho base S ----
function unit(n, S, cur, percentOf) {
  if (!n) return 0;
  return n.u === "em" ? n.v * cur : n.u === "%" ? n.v / 100 * percentOf : n.v;
}
function sizeOf(st, S) {
  var n = st.size, s = !n ? S : n.u === "em" ? S * n.v : n.u === "%" ? S * n.v / 100 : n.u === "+" ? S + n.v : n.v;
  return Math.max(0, s) * st.mult;
}
function lineWidth(st, S) {
  var w = RECT_W - unit(st.marginL, S, sizeOf(st, S), RECT_W) - unit(st.marginR, S, sizeOf(st, S), RECT_W);
  return st.width ? Math.min(w, unit(st.width, S, sizeOf(st, S), RECT_W)) : w;
}

function layout(items, S) {
  var lines = [], fits = true, em = S * 0.01;
  var line, x, firstWord, save, i = 0, last = items.length ? items[items.length - 1].st : null;

  function open(st) {
    line = { glyphs: [], firstItem: i, lastVisible: -1, st: st, driven: null, breakSt: null };
    x = st ? unit(st.indent, S, sizeOf(st, S), RECT_W) : 0;
    firstWord = true; save = null;
  }
  function close(breakSt) {
    line.breakSt = breakSt; lines.push(line);
  }
  open(items.length ? items[0].st : null);

  while (i < items.length) {
    var it = items[i], st = it.st, size = sizeOf(st, S);
    if (it.k === "pos") { x = unit(it.n, S, size, RECT_W); i++; continue; }
    if (it.k === "space") { x += unit(it.n, S, size, RECT_W); i++; continue; }
    if (it.k === "nl") {
      if (!line.glyphs.length) line.empty = { size: size };                  // linha vazia: vale a altura da fonte corrente
      close(st); i++; open(i < items.length ? items[i].st : st); continue;
    }
    var sprite = it.k === "sprite", face = sprite ? null : FACES[it.face];
    var adv = sprite ? 1.15 : advanceOf(it.face, it.ch, it.cp), space = !sprite && isSpace(it.cp);
    var prev = line.glyphs.length ? line.glyphs[line.glyphs.length - 1] : null;
    if (!sprite && it.face === "norse" && prev && prev.it.k === "c" && prev.it.face === "norse" && prev.size === size && prev.xAfterRaw === x && !st.mspace) {
      var k = kernOf(prev.it.ch, it.ch) * size; x += k; prev.kerned = k;
    }
    // a checagem do TMP: |x| + avanco do glifo, sem o <scale>; espaco nunca quebra, o 1o da linha tambem nao
    if (!space && Math.abs(x) + adv * size > lineWidth(st, S) + 0.0001 && line.glyphs.length > 0) {
      if (firstWord && S > SIZE_MIN) fits = false;
      var back = save || { i: i - 1, len: line.glyphs.length, lastVisible: line.lastVisible };
      line.glyphs.length = back.len; line.lastVisible = back.lastVisible;
      i = back.i + 1;
      close(items[back.i].st); open(items[i].st);
      line.wrapped = true;
      continue;
    }
    var extra = (sprite ? 0 : face.spacing) * em + unit(st.cspace, S, size, 0), mono = unit(st.mspace, S, size, 0);
    var gx = mono ? x + mono / 2 - adv * size * st.fx / 2 : x;
    var g = { it: it, x: gx, size: size, adv: adv * size * st.fx, extra: extra, space: space,
              voffset: unit(st.voffset, S, size, 0) + st.shift * size };
    x += mono ? mono + extra : g.adv + extra;
    g.xAfter = x; g.xAfterRaw = x;
    line.glyphs.push(g);
    if (!space) line.lastVisible = line.glyphs.length - 1;
    // ponto de quebra: depois de espaco ou hifen; enquanto for a 1a palavra da linha, qualquer caractere
    var breakable = (space && it.cp !== 0xA0 && !st.nobr) || it.cp === 45 || it.cp === 0xAD;
    if (breakable) firstWord = false;
    if (breakable || firstWord) save = { i: i, len: line.glyphs.length, lastVisible: line.lastVisible };
    i++;
  }
  close(null);
  if (lines.length > 1 && !lines[lines.length - 1].glyphs.length) lines.pop();   // texto que termina em \n: a linha vazia nao conta

  // metricas verticais: so conta quem nao e espaco (ou o 1o caractere da linha)
  var offset = 0, textAsc = 0, lastDesc = 0;
  lines.forEach(function (ln, index) {
    var asc = -1e9, desc = 1e9;
    ln.glyphs.forEach(function (g, gi) {
      if (g.space && gi > 0) return;
      var f = g.it.k === "sprite" ? { asc: 0.95, desc: -0.2 } : FACES[g.it.face], a = f.asc * g.size, d = f.desc * g.size;
      asc = Math.max(asc, a, a + g.voffset); desc = Math.min(desc, d, d + g.voffset);
    });
    if (asc === -1e9) { var s = ln.empty ? ln.empty.size : S; asc = FACES.norse.asc * s; desc = FACES.norse.desc * s; }
    ln.asc = asc; ln.desc = desc;
    if (index > 0) {
      var before = lines[index - 1], bst = before.breakSt, lh = bst ? bst.lineHeight : null;
      offset += lh ? (lh.u === "%" ? LINE_HEIGHT_EM * S * lh.v / 100 : unit(lh, S, sizeOf(bst, S), 0)) : -before.desc + asc;
    }
    ln.offset = offset;
    if (index === 0) textAsc = asc;
    if (ln.lastVisible >= 0 && textAsc + offset - desc > RECT_H + 0.0001 && S > SIZE_MIN) fits = false;
    lastDesc = desc - offset;
  });

  // alinhamento: a linha e centrada pelo ultimo caractere VISIVEL (maxAdvance), nao pelo que sobra
  // meio: o bloco que vai do topo da 1a linha ao pe da ultima fica centrado na area de texto
  var anchor = -(textAsc + lastDesc) / 2;
  lines.forEach(function (ln) {
    var lv = ln.lastVisible >= 0 ? ln.glyphs[ln.lastVisible] : ln.glyphs[ln.glyphs.length - 1];
    var maxAdvance = lv ? lv.xAfter - lv.extra : 0, st = lv ? lv.it.st : (ln.st || last);
    var w = st ? lineWidth(st, S) : RECT_W, left = st ? unit(st.marginL, S, sizeOf(st, S), RECT_W) : 0, align = st ? st.align : "center";
    ln.dx = -RECT_W / 2 + left + (align === "left" || align === "justified" || align === "flush" ? 0 : align === "right" ? w - maxAdvance : w / 2 - maxAdvance / 2);
    ln.baseline = anchor - ln.offset;                     // y para cima, 0 = meio da area de texto
  });
  return { lines: lines, fits: fits, size: S };
}

// A mesma bissecao do TMP: parte de 1, sobe enquanto cabe, passos arredondados a 0,05.
function autoSize(items) {
  var S = SIZE_MIN, lo = SIZE_MIN, hi = SIZE_MAX, result = null, guard = 0;
  while (guard++ < 100) {
    result = layout(items, S);
    if (!result.fits && S > SIZE_MIN) { hi = S; S = Math.max(Math.floor((S - Math.max((S - lo) / 2, 0.05)) * 20 + 0.5) / 20, SIZE_MIN); continue; }
    if (hi - lo > 0.051 && S < SIZE_MAX) { lo = S; S = Math.min(Math.floor((S + Math.max((hi - S) / 2, 0.05)) * 20 + 0.5) / 20, SIZE_MAX); continue; }
    break;
  }
  return result;
}

// ---- blocos: desenhados como retangulos, sem depender de fonte ----
var QUADS = { 0x2596: [0, 0, 1, 0], 0x2597: [0, 0, 0, 1], 0x2598: [1, 0, 0, 0], 0x2599: [1, 0, 1, 1], 0x259A: [1, 0, 0, 1], 0x259B: [1, 1, 1, 0],
              0x259C: [1, 1, 0, 1], 0x259D: [0, 1, 0, 0], 0x259E: [0, 1, 1, 0], 0x259F: [0, 1, 1, 1] };
// celula do bloco no Noto Sans JP: 1 em de lado, de -0,12 a 0,88 da linha de base
function blockRects(cp) {
  var B = -0.12, T = 0.88, e;
  if (cp === 0x2588) return [[0, B, 1, T, 1]];
  if (cp === 0x2580) return [[0, 0.38, 1, T, 1]];
  if (cp >= 0x2581 && cp <= 0x2587) return [[0, B, 1, B + (cp - 0x2580) / 8, 1]];
  if (cp >= 0x2589 && cp <= 0x258F) return [[0, B, (0x2590 - cp) / 8, T, 1]];
  if (cp === 0x2590) return [[0.5, B, 1, T, 1]];
  if (cp >= 0x2591 && cp <= 0x2593) return [[0, B, 1, T, (cp - 0x2590) / 4]];
  if (cp === 0x2594) return [[0, T - 0.125, 1, T, 1]];
  if (cp === 0x2595) return [[0.875, B, 1, T, 1]];
  if ((e = QUADS[cp])) {
    var r = [];
    if (e[0]) r.push([0, 0.38, 0.5, T, 1]); if (e[1]) r.push([0.5, 0.38, 1, T, 1]);
    if (e[2]) r.push([0, B, 0.5, 0.38, 1]); if (e[3]) r.push([0.5, B, 1, 0.38, 1]);
    return r;
  }
  return null;
}

// ---- cena: unidades da placa, y para cima, origem no meio da area de texto ----
// O material da placa e iluminado e corta a cor pela metade; LIGHT e a luz de cena que sobra num
// interior com tocha. O sem iluminacao sai como escrito e e o que alimenta o bloom.
var LIGHT = 0.8, SPRITE = { asc: 0.95, desc: -0.2 };
function lit(c) { var m = 0.5 * LIGHT; return [Math.round(c[0] * m), Math.round(c[1] * m), Math.round(c[2] * m)]; }
function css(rgb, a) { return "rgba(" + rgb[0] + "," + rgb[1] + "," + rgb[2] + "," + a + ")"; }
function metrics(g) { return g.it.k === "sprite" ? SPRITE : FACES[g.it.face]; }
function grow(ink, x0, y0, x1, y1) {
  if (!ink) return { x0: x0, y0: y0, x1: x1, y1: y1 };
  ink.x0 = Math.min(ink.x0, x0); ink.y0 = Math.min(ink.y0, y0); ink.x1 = Math.max(ink.x1, x1); ink.y1 = Math.max(ink.y1, y1);
  return ink;
}

function inkOf(result) {
  var ink = null;
  result.lines.forEach(function (ln) {
    ln.glyphs.forEach(function (g) {
      var st = g.it.st, f = metrics(g);
      if (g.space && !st.mark) return;
      if (g.it.k === "c" && Math.min(st.color[3], st.alpha) === 0 && !st.mark) return;
      ink = grow(ink, ln.dx + Math.min(g.x, g.x + g.adv), ln.baseline + g.voffset + f.desc * g.size,
                      ln.dx + Math.max(g.x, g.x + g.adv), ln.baseline + g.voffset + f.asc * g.size);
    });
  });
  return ink;
}

// Uma passada por material: a iluminada vai direto na cena, a sem iluminacao numa camada propria.
function drawText(ctx, result, unlit) {
  // o mark e um retangulo no mesh da placa: sempre iluminado, sempre meio transparente, e so nasce
  // ate o ultimo caractere visivel da linha
  if (!unlit) result.lines.forEach(function (ln) {
    ln.glyphs.forEach(function (g, gi) {
      var st = g.it.st, f = metrics(g);
      if (!st.mark || gi > ln.lastVisible) return;
      ctx.fillStyle = css(lit(st.mark), st.mark[3] / 255 * 0.5);
      ctx.fillRect(ln.dx + g.x, ln.baseline + g.voffset + f.desc * g.size, g.xAfter - g.x - g.extra + (gi < ln.lastVisible ? g.extra : 0), (f.asc - f.desc) * g.size);
    });
  });
  result.lines.forEach(function (ln) {
    ln.glyphs.forEach(function (g) {
      var it = g.it, st = it.st, sprite = it.k === "sprite";
      if ((sprite || st.unlit) !== unlit) return;
      var alpha = Math.min(st.color[3], st.alpha) / 255, x = ln.dx + g.x, y = ln.baseline + g.voffset;
      if (sprite) {
        ctx.fillStyle = css(it.color, it.color[3] / 255);
        ctx.beginPath(); ctx.arc(x + g.adv / 2, y + g.size * 0.36, g.size * 0.5, 0, Math.PI * 2); ctx.fill();
        return;
      }
      var color = st.unlit ? st.color : lit(st.color);
      if (st.underline || st.strike) {
        ctx.fillStyle = css(color, alpha);
        if (st.underline) ctx.fillRect(x, y - 0.15 * g.size, g.xAfter - g.x, 0.05 * g.size);
        if (st.strike) ctx.fillRect(x, y + 0.275 * g.size, g.xAfter - g.x, 0.05 * g.size);
      }
      if (g.space || alpha === 0 || g.size <= 0) return;
      var rects = it.face === "block" ? blockRects(it.cp) : null;
      ctx.save();
      ctx.translate(x, y);
      if (st.rot) { ctx.translate(g.adv / 2, g.size * 0.35); ctx.rotate(st.rot * Math.PI / 180); ctx.translate(-g.adv / 2, -g.size * 0.35); }
      ctx.fillStyle = css(color, alpha);
      if (rects) {
        rects.forEach(function (q) { ctx.globalAlpha = q[4]; ctx.fillRect(q[0] * g.adv, q[1] * g.size, (q[2] - q[0]) * g.adv, (q[3] - q[1]) * g.size); });
      } else {
        ctx.scale(g.size / 100 * st.fx, -g.size / 100);
        if (st.italic) ctx.transform(1, 0, -ITALIC_SLANT, 1, 0, 0);
        ctx.font = FACES[it.face].font;
        if (it.face === "block") { ctx.textAlign = "center"; ctx.translate(50, 0); }
        if (st.outline) { ctx.lineJoin = "round"; ctx.lineWidth = 14; ctx.strokeStyle = "#000"; ctx.strokeText(it.ch, 0, 0); }
        ctx.fillText(it.ch, 0, 0);
      }
      ctx.restore();
    });
  });
}

function textScene(source, opts) {
  var parsed = parse(source, opts.macros), result = autoSize(parsed.items);
  request(parsed.items);
  return { ink: inkOf(result), paint: function (board, glow) { drawText(board, result, false); drawText(glow, result, true); } };
}

// ---- a tabua: textura de baixa resolucao sem suavizar, como as do jogo ----
var woodCanvas = null;
function wood() {
  if (woodCanvas) return woodCanvas;
  var TW = 40, TH = 20, c = document.createElement("canvas"), seed = 20;
  c.width = TW; c.height = TH;
  var x = c.getContext("2d"), data = x.createImageData(TW, TH), px = data.data;
  function rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; }
  for (var r = 0; r < TH; r++) {
    var row = 0.84 + rnd() * 0.26, run = 0, streak = 1;
    for (var col = 0; col < TW; col++) {
      if (run-- <= 0) { run = 2 + Math.floor(rnd() * 7); streak = 0.9 + rnd() * 0.2; }     // veio corre na horizontal
      var v = row * streak * (0.95 + rnd() * 0.1);
      if (r === 0) v *= 1.4; else if (r === TH - 1) v *= 0.55;                              // quina de cima pega luz
      if (col === 0 || col === TW - 1) v *= 0.75;
      var o = (r * TW + col) * 4;
      px[o] = Math.min(255, 92 * v); px[o + 1] = Math.min(255, 58 * v); px[o + 2] = Math.min(255, 32 * v); px[o + 3] = 255;   // medido num print ao entardecer
    }
  }
  x.putImageData(data, 0, 0);
  return (woodCanvas = c);
}

// ---- camera: a tabua fica parada enquanto o desenho cabe; quando nao cabe, recua em degraus ----
var FRAME = 0.64, FRAME_H = 0.52, STEP = 1.5, MAX_STEPS = 7, PAD = 8;
function camera(W, H, ink, fit) {
  var base = Math.min(W * FRAME / BOARD_W, H * FRAME_H / BOARD_H), z = 1, k = 0;
  if (fit) {
    var ex = ink ? Math.max(BOARD_W / 2, Math.abs(ink.x0), Math.abs(ink.x1)) : BOARD_W / 2;
    var ey = ink ? Math.max(BOARD_H / 2, Math.abs(ink.y0), Math.abs(ink.y1)) : BOARD_H / 2;
    return { scale: Math.min((W / 2 - 2) / ex, (H / 2 - 2) / ey), clipped: false };
  }
  var needX = ink ? Math.max(Math.abs(ink.x0), Math.abs(ink.x1)) : 0, needY = ink ? Math.max(Math.abs(ink.y0), Math.abs(ink.y1)) : 0;
  function cut() { return needX * base * z > W / 2 - PAD || needY * base * z > H / 2 - PAD; }
  while (k < MAX_STEPS && cut()) { z /= STEP; k++; }
  return { scale: base * z, clipped: cut() };
}

var pool = [];
function scratch(i, w, h) {
  var c = pool[i] || (pool[i] = document.createElement("canvas")), x;
  if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
  x = c.getContext("2d");
  x.setTransform(1, 0, 0, 1, 0, 0); x.globalCompositeOperation = "source-over"; x.globalAlpha = 1;
  x.clearRect(0, 0, w, h);
  return x;
}
function aim(ctx, pw, ph, s) { ctx.setTransform(s, 0, 0, -s, pw / 2, ph / 2); }

function draw(canvas, st) {
  var dpr = Math.min(window.devicePixelRatio || 1, 2), W = canvas.clientWidth || canvas.width / dpr, H = canvas.clientHeight || canvas.height / dpr;
  if (!W || !H || !st.scene) return;
  var pw = Math.round(W * dpr), ph = Math.round(H * dpr), s = st.scale * dpr;
  if (canvas.width !== pw || canvas.height !== ph) { canvas.width = pw; canvas.height = ph; }
  var ctx = canvas.getContext("2d");
  ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.globalCompositeOperation = "source-over"; ctx.globalAlpha = 1;
  var ground = ctx.createRadialGradient(pw / 2, ph * 0.45, 0, pw / 2, ph * 0.45, Math.max(pw, ph) * 0.62);
  ground.addColorStop(0, "#2A373C"); ground.addColorStop(1, "#0C1215");
  ctx.fillStyle = ground; ctx.fillRect(0, 0, pw, ph);

  aim(ctx, pw, ph, s);
  ctx.save();
  ctx.shadowColor = "rgba(0,0,0,0.6)"; ctx.shadowBlur = 0.9 * s; ctx.shadowOffsetY = 0.3 * s;
  ctx.fillStyle = "#2A1D11"; ctx.fillRect(-BOARD_W / 2, -BOARD_H / 2, BOARD_W, BOARD_H);
  ctx.restore();
  ctx.save();
  ctx.imageSmoothingEnabled = s < 3;                     // de longe o texel some e o serrilhado so atrapalha
  ctx.translate(-BOARD_W / 2, BOARD_H / 2); ctx.scale(1, -1);
  ctx.drawImage(wood(), 0, 0, BOARD_W, BOARD_H);
  ctx.restore();

  var glow = scratch(0, pw, ph);
  aim(glow, pw, ph, s);
  st.scene.paint(ctx, glow);

  // bloom: a camada sem iluminacao encolhida pela metade varias vezes (um desfoque barato que todo
  // navegador faz), elevada ao quadrado para so o que e claro estourar, e somada de volta
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.drawImage(glow.canvas, 0, 0);
  var level = glow.canvas, levels = [], w = pw, h = ph;
  for (var i = 1; i <= 4; i++) {
    w = Math.max(1, Math.round(w / 2)); h = Math.max(1, Math.round(h / 2));
    var x = scratch(i, w, h);
    x.drawImage(level, 0, 0, w, h);
    if (i === 2) { x.globalCompositeOperation = "multiply"; x.drawImage(level, 0, 0, w, h); }
    level = x.canvas; levels.push(level);
  }
  ctx.globalCompositeOperation = "lighter"; ctx.imageSmoothingEnabled = true;
  ctx.globalAlpha = 0.5; ctx.drawImage(levels[1], 0, 0, pw, ph);
  ctx.globalAlpha = 0.7; ctx.drawImage(levels[3], 0, 0, pw, ph);
  ctx.globalCompositeOperation = "source-over"; ctx.globalAlpha = 1;
}

var reduced = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
function retarget(canvas, st, jump) {
  var cam = camera(canvas.clientWidth, canvas.clientHeight, st.scene ? st.scene.ink : null);
  st.target = cam.scale; st.clipped = cam.clipped;
  if (jump || reduced || st.scale == null) st.scale = cam.scale;
  if (st.raf) return;
  (function tick() {
    st.raf = 0;
    if (Math.abs(st.scale - st.target) < st.target * 0.004) st.scale = st.target;
    else { st.scale += (st.target - st.scale) * 0.35; st.raf = requestAnimationFrame(tick); }
    draw(canvas, st);
  })();
}
function stateOf(canvas) {
  if (canvas.__sign) return canvas.__sign;
  var st = canvas.__sign = { scale: null, target: 1, scene: null, raf: 0, clipped: false };
  if (window.ResizeObserver) new ResizeObserver(function () { retarget(canvas, st, true); }).observe(canvas);
  return st;
}

// O texto como o servidor gravou (ja com o desenho do icone no lugar do codigo).
function render(canvas, text, opts) {
  opts = opts || {};
  var st = stateOf(canvas);
  st.scene = textScene(text || "", opts);
  retarget(canvas, st, false);
  var ink = st.scene.ink;
  return { overflow: !!ink && (ink.x0 < -BOARD_W / 2 || ink.x1 > BOARD_W / 2 || ink.y0 < -BOARD_H / 2 || ink.y1 > BOARD_H / 2),
           clipped: st.clipped, width: ink ? (ink.x1 - ink.x0) * 0.05 : 0, height: ink ? (ink.y1 - ink.y0) * 0.05 : 0 };
}

// ---- fontes: redesenha quando chegam (emoji vem em fatias por faixa de unicode) ----
var asked = {}, listeners = [];
function request(items) {
  if (!document.fonts || !document.fonts.load) return;
  var need = { norse: "A", latin: "", emoji: "" };
  items.forEach(function (it) { if (it.k === "c" && need[it.face] != null && need[it.face].indexOf(it.ch) < 0) need[it.face] += it.ch; });
  Object.keys(need).forEach(function (face) {
    var str = need[face], key = face + str;
    if (!str || asked[key]) return;
    asked[key] = true;
    document.fonts.load(FACES[face].font.replace("100px", "32px"), str).then(function (loaded) {
      if (!loaded || !loaded.length) return;
      epoch++; widths = {};
      listeners.forEach(function (fn) { fn(); });
    }, function () {});
  });
}

// Miniatura: desenha uma vez, sem animacao nem observador, no tamanho do proprio canvas.
function still(canvas, text, opts) {
  var st = { scene: textScene(text || "", opts || {}) };
  var dpr = Math.min(window.devicePixelRatio || 1, 2), W = canvas.clientWidth || canvas.width / dpr, H = canvas.clientHeight || canvas.height / dpr;
  st.scale = camera(W, H, st.scene.ink, true).scale;
  draw(canvas, st);
}

// unknown = as tags que o jogo nao entende (saem escritas na placa), para o campo de texto marcar
export const SignSim = { render: render, still: still, onFonts: function (fn) { listeners.push(fn); },
                         unknown: function (source, macros) { return parse(source, macros).unknown; } };
