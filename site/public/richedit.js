// Editor visual das placas: um contenteditable que nao deixa o navegador mexer no HTML. Cada tecla
// vira uma troca nas pecas (signrich.js), o codigo e reescrito e o HTML e refeito dele; o cursor anda
// em indices de peca. So a composicao de acento/IME passa pelo navegador, e e lida de volta no fim.
import { commonStyle, cssColor, parse, sameStyle, serialize, withStyle } from './signrich.js';

const MERGE_MS = 900;

// Tamanho da letra no editor: a mesma proporcao da placa (PX por unidade do jogo), com teto para caber.
// Sem <size>, vale o que o auto-size da placa escolheu (setAutoSize, vindo da previa).
const PX = 10;
const sizePx = (z) => Math.round(Math.max(10, Math.min(80, Number(z) * PX)));

export class RichEditor {
  // env: o de signrich.parse. chip(atom) -> elemento do chip. onChange(src, how). onSelect().
  constructor(root, { env, chip, onChange, onSelect }) {
    this.root = root;
    this.env = env;
    this.chip = chip;
    this.onChange = onChange;
    this.onSelect = onSelect;
    this.atoms = [];
    this.end = {};
    this.src = '';
    this.sel = { start: 0, end: 0 };
    this.pending = null;
    this.undoStack = [];
    this.redoStack = [];
    this.lastEdit = null;
    this.composing = false;

    root.contentEditable = 'true';
    root.spellcheck = false;
    root.setAttribute('role', 'textbox');
    root.setAttribute('aria-multiline', 'true');
    root.addEventListener('beforeinput', (e) => this.beforeInput(e));
    root.addEventListener('input', () => { if (!this.composing) this.readBack(); });
    root.addEventListener('compositionstart', () => { this.composing = true; });
    root.addEventListener('compositionend', () => { this.composing = false; this.readBack(); });
    root.addEventListener('paste', (e) => this.paste(e));
    root.addEventListener('copy', (e) => this.copy(e, false));
    root.addEventListener('cut', (e) => this.copy(e, true));
    root.addEventListener('dragstart', (e) => e.preventDefault());
    root.addEventListener('drop', (e) => e.preventDefault());
    root.addEventListener('keydown', (e) => this.keyDown(e));
    document.addEventListener('selectionchange', () => this.selectionChanged());
  }

  // ---- texto de fora (codigo, link, botoes) ----

  // record: entra no desfazer. keep: tenta manter o cursor no mesmo ponto do codigo.
  setSource(src, { record = false } = {}) {
    if (src === this.src && this.atoms.length) return;
    if (record) this.remember('set');
    const at = this.sel ? serialize(this.atoms).pos[this.sel.end] ?? src.length : src.length;
    this.load(src);
    const p = Math.min(this.atomIndex(Math.min(at, src.length)), this.atoms.length);
    this.sel = { start: p, end: p };
    this.render();
    if (record) this.emit('set');
  }

  // O catalogo chegou (ou mudou): o mesmo codigo pode virar chip agora.
  refresh() {
    const at = serialize(this.atoms).pos;
    const s = at[this.sel.start];
    const e = at[this.sel.end];
    this.load(this.src);
    this.sel = { start: this.atomIndex(s), end: this.atomIndex(e) };
    this.render();
  }

  setAutoSize(units) {
    if (units > 0) this.root.style.setProperty('--auto', `${Math.max(16, Math.min(60, units * PX))}px`);
  }

  load(src) {
    const r = parse(src, this.env);
    this.atoms = r.atoms;
    this.end = r.end;
    this.src = src;
  }

  atomIndex(offset) {
    let n = 0;
    while (n < this.atoms.length && this.atoms[n].at < offset) n++;
    return n;
  }

  // ---- edicao ----

  // Troca [start, end) por novas pecas; depois reescreve o codigo e le de novo (tag digitada vira
  // estilo, :codigo: conhecido vira chip).
  replace(start, end, inserted, how) {
    this.remember(how);
    const atoms = this.atoms.slice(0, start).concat(inserted, this.atoms.slice(end));
    this.commit(atoms, start + inserted.length, how);
  }

  commit(atoms, caret, how, selStart = caret) {
    const { src, pos } = serialize(atoms);
    const before = atoms.length;
    this.load(src);
    const changed = this.atoms.length !== before;
    const s = this.atomIndex(pos[selStart]);
    const e = this.atomIndex(pos[caret]);
    this.sel = { start: s, end: e };
    // Tag digitada a mao: o que vem depois do cursor segue o estilo dela.
    this.pending = changed && s === e ? (this.atoms[e]?.st ?? this.end) : null;
    this.render();
    this.emit(how);
  }

  // O estilo da letra antes (ou, no comeco, da de depois). Chip nao passa o dele: o <size> de um icone e o
  // lado do icone, nao tamanho de letra.
  typingStyle(at) {
    if (this.pending) return this.pending;
    for (let n = at - 1; n >= 0; n--) if (this.atoms[n].t !== 'x') return this.atoms[n].st;
    for (let n = at; n < this.atoms.length; n++) if (this.atoms[n].t !== 'x') return this.atoms[n].st;
    return this.atoms.length ? {} : this.end;
  }

  chars(text, st) {
    const out = [];
    for (const ch of text.replace(/\r\n?/g, '\n')) out.push(ch === '\n' ? { t: 'n', st } : { t: 'c', ch, st });
    return out;
  }

  insertText(text, range = this.sel, how = 'type') {
    const st = this.typingStyle(range.start);
    this.replace(range.start, range.end, this.chars(text, st), how);
  }

  // Codigo colado ou de botao: as tags dele valem sobre o estilo de onde entra.
  insertSource(text, range = this.sel) {
    const base = this.typingStyle(range.start);
    const { atoms, end } = parse(text.replace(/\r\n?/g, '\n'), this.env);
    const merged = atoms.map((a) => ({ ...a, st: { ...base, ...a.st } }));
    if (!merged.length && !sameStyle(end, {})) {
      this.pending = { ...base, ...end };
      this.onSelect?.();
      return;
    }
    this.replace(range.start, range.end, merged, 'insert');
  }

  deleteRange(range, how) {
    if (range.start === range.end) return;
    this.replace(range.start, range.end, [], how);
  }

  // Aplica ao trecho escolhido, ou guarda para o que for digitado se o cursor estiver parado.
  // how 'pick': o seletor de cor andando, que entra no desfazer uma vez so.
  applyStyle(key, value, how = 'style') {
    const { start, end } = this.sel;
    if (start === end) {
      this.pending = withStyle(this.typingStyle(start), key, value);
      this.onSelect?.();
      return;
    }
    this.remember(how);
    const atoms = this.atoms.map((a, n) => (n >= start && n < end ? { ...a, st: withStyle(a.st, key, value) } : a));
    this.commit(atoms, end, how, start);
  }

  // Tags da colinha em volta do trecho escolhido (ou so a de abrir, no cursor).
  wrapSource(open, close) {
    const { start, end } = this.sel;
    const inner = serialize(this.atoms.slice(start, end)).src;
    const base = this.typingStyle(start);
    const { atoms, end: tail } = parse(open + inner + (inner ? close : ''), this.env);
    if (!atoms.length) {
      this.pending = { ...base, ...tail };
      this.onSelect?.();
      return;
    }
    this.replace(start, end, atoms, 'insert');
  }

  // Liga/desliga: se todo o trecho ja tem, tira.
  toggle(key, on = true) {
    const cur = this.currentStyle()[key];
    this.applyStyle(key, cur === on ? null : on);
  }

  currentStyle() {
    const { start, end } = this.sel;
    const empty = { c: null, z: null, l: null, m: null, i: null, u: null, s: null };
    if (start === end) return { ...empty, ...this.typingStyle(start) };
    return commonStyle(this.atoms, start, end) ?? { ...empty, ...this.typingStyle(start) };
  }

  // Texto (so letras) logo antes do cursor: o autocompletar do : le daqui.
  textBefore(max) {
    let out = '';
    for (let n = this.sel.end - 1; n >= 0 && out.length < max; n--) {
      const a = this.atoms[n];
      if (a.t !== 'c') break;
      out = a.ch + out;
    }
    return out;
  }

  // ---- desfazer ----

  remember(how) {
    const now = Date.now();
    const merge = (how === 'type' || how === 'pick') && this.lastEdit?.how === how && now - this.lastEdit.time < MERGE_MS;
    this.lastEdit = { how, time: now };
    if (merge) return;
    this.undoStack.push({ src: this.src, sel: { ...this.sel } });
    if (this.undoStack.length > 200) this.undoStack.shift();
    this.redoStack = [];
  }

  history(from, to) {
    const state = from.pop();
    if (!state) return;
    to.push({ src: this.src, sel: { ...this.sel } });
    this.lastEdit = null;
    this.pending = null;
    this.load(state.src);
    this.sel = { start: Math.min(state.sel.start, this.atoms.length), end: Math.min(state.sel.end, this.atoms.length) };
    this.render();
    this.emit('history');
  }
  undo() { this.history(this.undoStack, this.redoStack); }
  redo() { this.history(this.redoStack, this.undoStack); }

  emit(how) {
    this.onChange?.(this.src, how);
    this.onSelect?.();
  }

  // ---- eventos do navegador ----

  beforeInput(e) {
    const t = e.inputType;
    if (t === 'insertCompositionText' || this.composing) return;
    e.preventDefault();
    let range = this.readSelection() ?? this.sel;
    const targets = e.getTargetRanges?.() ?? [];
    if (targets.length && (t.startsWith('delete') || t === 'insertReplacementText')) {
      const r = targets[0];
      const a = this.posOf(r.startContainer, r.startOffset);
      const b = this.posOf(r.endContainer, r.endOffset);
      if (a != null && b != null) range = { start: Math.min(a, b), end: Math.max(a, b) };
    }
    switch (t) {
      case 'insertText':
      case 'insertReplacementText': {
        const text = e.data ?? e.dataTransfer?.getData('text/plain') ?? '';
        if (text) this.insertText(text, range, t === 'insertText' && !/\s/.test(text) ? 'type' : 'insert');
        return;
      }
      case 'insertParagraph':
      case 'insertLineBreak':
        this.replace(range.start, range.end, [{ t: 'n', st: this.typingStyle(range.start) }], 'insert');
        return;
      case 'insertFromPaste':
      case 'insertFromDrop':
        this.insertSource(e.dataTransfer?.getData('text/plain') ?? '', range);
        return;
      case 'historyUndo': this.undo(); return;
      case 'historyRedo': this.redo(); return;
      case 'formatItalic': this.toggle('i'); return;
      case 'formatUnderline': this.toggle('u'); return;
      case 'formatStrikeThrough': this.toggle('s'); return;
    }
    if (t.startsWith('delete')) {
      if (range.start === range.end) {
        // Sem alvo do navegador: uma peca para o lado da tecla.
        if (t.includes('Backward') && range.start > 0) range = { start: range.start - 1, end: range.end };
        else if (t.includes('Forward') && range.end < this.atoms.length) range = { start: range.start, end: range.end + 1 };
      }
      this.deleteRange(range, 'delete');
    }
  }

  keyDown(e) {
    const mod = e.ctrlKey || e.metaKey;
    if (!mod || e.altKey) return;
    const k = e.key.toLowerCase();
    if (k === 'z' && !e.shiftKey) this.undo();
    else if ((k === 'z' && e.shiftKey) || k === 'y') this.redo();
    else if (k === 'i') this.toggle('i');
    else if (k === 'u') this.toggle('u');
    else if (k === 'b') { /* a placa ja e negrito */ } else return;
    e.preventDefault();
  }

  paste(e) {
    e.preventDefault();
    const text = e.clipboardData?.getData('text/plain') ?? '';
    this.insertSource(text, this.readSelection() ?? this.sel);
  }

  // Copiar leva o codigo do trecho (com as tags), que cola igual aqui, no modo codigo ou no jogo.
  copy(e, cut) {
    const range = this.readSelection() ?? this.sel;
    if (range.start === range.end) return;
    e.preventDefault();
    e.clipboardData?.setData('text/plain', serialize(this.atoms.slice(range.start, range.end)).src);
    if (cut) this.deleteRange(range, 'delete');
  }

  // Composicao terminou (ou algo passou): le o HTML de volta em pecas. O cursor sai da mesma leitura,
  // porque o HTML que o navegador mexeu nao bate mais com os indices guardados nele.
  readBack() {
    const atoms = [];
    const s = document.getSelection();
    const focus = s?.rangeCount ? [s.focusNode, s.focusOffset] : null;
    let caret = null;
    const walk = (node, st) => {
      node.childNodes.forEach((child, k) => {
        if (focus && focus[0] === node && focus[1] === k) caret = atoms.length;
        if (child.nodeType === 3) {
          let n = 0;
          for (const ch of child.data) {
            if (focus && focus[0] === child && n === focus[1]) caret = atoms.length;
            n += ch.length;
            if (ch !== '\u200b') atoms.push({ t: 'c', ch, st });
          }
          if (focus && focus[0] === child && n <= focus[1]) caret = atoms.length;
        } else if (child.nodeType === 1) {
          if (child.dataset.end != null) return;
          if (child.dataset.atom != null) { const a = this.atoms[+child.dataset.atom]; if (a) atoms.push(a); return; }
          if (child.tagName === 'BR') { atoms.push({ t: 'n', st }); return; }
          if (/^(DIV|P)$/.test(child.tagName) && atoms.length && atoms.at(-1).t !== 'n') atoms.push({ t: 'n', st });
          walk(child, child.dataset.st ? JSON.parse(child.dataset.st) : st);
        }
      });
      if (focus && focus[0] === node && focus[1] >= node.childNodes.length && caret == null) caret = atoms.length;
    };
    walk(this.root, {});
    caret ??= atoms.length;
    this.remember('type');
    this.commit(atoms, caret, 'type');
  }

  selectionChanged() {
    if (this.composing) return;
    const sel = this.readSelection();
    if (!sel) return;
    if (sel.start !== this.sel.start || sel.end !== this.sel.end) {
      this.sel = sel;
      this.pending = null;
      this.lastEdit = null;
    }
    this.onSelect?.();
  }

  hasFocus() { return this.root.contains(document.activeElement) || document.activeElement === this.root; }

  focus() {
    if (!this.hasFocus()) this.root.focus({ preventScroll: true });
    this.placeSelection();
  }

  // ---- HTML <-> indices ----

  render() {
    const root = this.root;
    root.textContent = '';
    let run = null;
    let runText = '';
    const flush = () => { if (run) run.append(document.createTextNode(runText)); run = null; runText = ''; };
    this.atoms.forEach((a, n) => {
      if (a.t === 'c') {
        if (run && sameStyle(JSON.parse(run.dataset.st), a.st)) {
          runText += a.ch;
          run.dataset.n = +run.dataset.n + 1;
          return;
        }
        flush();
        run = document.createElement('span');
        run.className = 'run';
        run.dataset.at = n;
        run.dataset.n = 1;
        run.dataset.st = JSON.stringify(a.st);
        paint(run, a.st);
        runText = a.ch;
        root.append(run);
        return;
      }
      flush();
      const el = a.t === 'n' ? document.createElement('br') : this.chip(a);
      el.dataset.at = n;
      el.dataset.atom = n;
      if (a.t === 'x') {
        el.contentEditable = 'false';
        paint(el, a.st, true);
      }
      root.append(el);
    });
    flush();
    // O navegador so mostra a ultima linha vazia (e o campo vazio) com um <br> no fim.
    if (!this.atoms.length || this.atoms.at(-1).t === 'n') {
      const br = document.createElement('br');
      br.dataset.end = '';
      root.append(br);
    }
    root.classList.toggle('is-empty', !this.atoms.length);
    if (this.hasFocus()) this.placeSelection();
  }

  // (no, deslocamento) do DOM -> indice de peca.
  posOf(node, offset) {
    const root = this.root;
    if (!root.contains(node)) return null;
    const startOf = (el) => (el && el.dataset?.at != null ? +el.dataset.at : this.atoms.length);
    const endOf = (el) => startOf(el) + (el.dataset.n ? +el.dataset.n : 1);
    if (node === root) return startOf(root.childNodes[offset]);
    let el = node.nodeType === 3 ? node.parentElement : node;
    while (el && el.parentElement !== root) el = el.parentElement;
    if (!el) return null;
    if (el.dataset.end != null) return this.atoms.length;
    if (el.dataset.atom != null) return node === el && offset === 0 ? startOf(el) : offset > 0 ? endOf(el) : startOf(el);
    if (node.nodeType === 3) {
      let units = offset;
      for (let s = node.previousSibling; s; s = s.previousSibling) units += s.textContent.length;
      let n = startOf(el);
      const last = endOf(el);
      while (n < last && units > 0) { units -= this.atoms[n].ch.length; n++; }
      return n;
    }
    return offset === 0 ? startOf(el) : endOf(el);
  }

  // indice de peca -> (no, deslocamento). Na fronteira entre dois trechos, fica no fim do primeiro.
  pointOf(pos) {
    const kids = this.root.childNodes;
    for (let k = 0; k < kids.length; k++) {
      const el = kids[k];
      if (el.dataset.end != null) return [this.root, k];
      const s = +el.dataset.at;
      if (el.dataset.n) {
        const e = s + +el.dataset.n;
        if (pos >= s && pos <= e) {
          let units = 0;
          for (let n = s; n < pos; n++) units += this.atoms[n].ch.length;
          return [el.firstChild, units];
        }
      } else if (s === pos) return [this.root, k];
    }
    return [this.root, kids.length];
  }

  readSelection() {
    const s = document.getSelection();
    if (!s || !s.rangeCount || !this.root.contains(s.anchorNode)) return null;
    const a = this.posOf(s.anchorNode, s.anchorOffset);
    const b = this.posOf(s.focusNode, s.focusOffset);
    if (a == null || b == null) return null;
    return { start: Math.min(a, b), end: Math.max(a, b), back: b < a };
  }

  placeSelection() {
    const s = document.getSelection();
    if (!s) return;
    const [an, ao] = this.pointOf(this.sel.back ? this.sel.end : this.sel.start);
    const [fn, fo] = this.pointOf(this.sel.back ? this.sel.start : this.sel.end);
    try { s.setBaseAndExtent(an, ao, fn, fo); } catch {}
  }

  select(start, end = start) {
    this.sel = { start, end };
    this.pending = null;
    this.focus();
  }

  // Retangulo do cursor na tela (para o autocompletar).
  caretRect() {
    const [node, offset] = this.pointOf(this.sel.end);
    const r = document.createRange();
    try { r.setStart(node, offset); } catch { return null; }
    r.collapse(true);
    const rects = r.getClientRects();
    if (rects.length) return rects[rects.length - 1];
    const el = node.nodeType === 1 ? node.childNodes[offset] ?? node : node.parentElement;
    return el.getBoundingClientRect?.() ?? null;
  }
}

function paint(el, st, chip) {
  const s = el.style;
  if (st.c) s.color = cssColor(st.c);
  if (st.z && !chip) s.fontSize = `${sizePx(st.z)}px`;
  if (st.m) s.backgroundColor = cssColor(st.m);
  if (st.i) s.fontStyle = 'italic';
  const deco = [st.u && 'underline', st.s && 'line-through'].filter(Boolean).join(' ');
  if (deco) s.textDecoration = deco;
  if (st.l) el.classList.add('unlit');
}
