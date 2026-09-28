// Escondidos: quem olha o site pode tirar do mapa uma base, bau, portal, cama, marcacao ou jogador. O servidor
// some com aquilo para todo mundo, menos para este navegador, que continua vendo (marcado) e pode mostrar de
// novo. O navegador e reconhecido por cookie; sem ele, por uma impressao basica (fingerprint) mandada aqui.

const KIND_NAMES = { base: 'base', chest: 'baú', portal: 'portal', bed: 'cama', pin: 'marcação', player: 'jogador' };

const WHAT_GOES = {
  base: 'As construções, baús, camas, portais e marcações da área — e quem estiver lá dentro — somem do site para todo mundo.',
  chest: 'O baú e o que tem dentro somem do mapa, da busca e das páginas para todo mundo.',
  portal: 'O portal some do mapa, e o par dele deixa de apontar para cá.',
  bed: 'A cama some do mapa e das páginas para todo mundo.',
  pin: 'A marcação some do mapa (e dos dias passados) para todo mundo.',
  player: 'A posição ao vivo e os rastros somem do site para todo mundo. O nome continua na lista de quem está online.',
};

export const kindName = (k) => KIND_NAMES[k] ?? k;

async function sha256(text) {
  const bytes = new TextEncoder().encode(text);
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

// Impressao basica: o que o navegador conta de si sem pedir permissao. Nao e unica no mundo, so o bastante
// para achar de volta o mesmo navegador entre os poucos amigos do servidor.
async function fingerprint() {
  const parts = [
    navigator.userAgent, navigator.language, (navigator.languages ?? []).join(','), navigator.platform,
    navigator.hardwareConcurrency, navigator.deviceMemory, navigator.maxTouchPoints,
    screen.width, screen.height, screen.colorDepth, window.devicePixelRatio,
    Intl.DateTimeFormat().resolvedOptions().timeZone,
  ];
  try {
    const c = document.createElement('canvas');
    c.width = 220;
    c.height = 40;
    const ctx = c.getContext('2d');
    ctx.textBaseline = 'top';
    ctx.font = '16px Arial';
    ctx.fillStyle = '#f60';
    ctx.fillRect(100, 1, 60, 20);
    ctx.fillStyle = '#069';
    ctx.fillText('Valheim ᚠᚢᚦ 🪓', 2, 14);
    parts.push(c.toDataURL());
  } catch {}
  try {
    const gl = document.createElement('canvas').getContext('webgl');
    const info = gl?.getExtension('WEBGL_debug_renderer_info');
    if (info) parts.push(gl.getParameter(info.UNMASKED_VENDOR_WEBGL), gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  } catch {}
  return sha256(parts.join('|'));
}

let fp = null;
let mine = [];
const listeners = new Set();

async function post(url, body) {
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ fp, ...body }) });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `${res.status}`);
  return data;
}

// Identidade antes de qualquer dado: o que chega do servidor depende de quem pede.
export const ready = (async () => {
  try {
    fp = await fingerprint();
  } catch {}
  try {
    const me = await post('api/me', {});
    mine = me.hides ?? [];
    if (me.recovered && mine.length) {
      queueMicrotask(() => toast(`Reconhecemos este navegador: ${mine.length === 1 ? 'seu escondido voltou' : `seus ${mine.length} escondidos voltaram`}.`));
    }
  } catch {}
})();

export const myHides = () => mine;
export const onHiddenChange = (fn) => listeners.add(fn);
const changed = () => listeners.forEach((fn) => fn(mine));

export const myHide = (id) => mine.find((h) => h.id === id) ?? null;
export const myPlayerHide = (name) => mine.find((h) => h.kind === 'player' && h.name === name) ?? null;
// Base que eu escondi e que contem (x, z).
export function myAreaAt(x, z) {
  return mine.find((h) => h.kind === 'base' && x >= h.box[0] && x <= h.box[2] && z >= h.box[1] && z <= h.box[3]) ?? null;
}

// ---------- confirmacao e aviso ----------

function node(tag, props = {}, ...children) {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === 'class') n.className = v;
    else if (k === 'text') n.textContent = v;
    else if (k.startsWith('on')) n.addEventListener(k.slice(2), v);
    else n.setAttribute(k, v);
  }
  n.append(...children.filter((c) => c != null));
  return n;
}

export const EYE_OFF = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 3l18 18"/><path d="M10.6 5.1A9.8 9.8 0 0 1 12 5c5 0 9 4.5 10 7-.4 1-1.2 2.3-2.4 3.5M6.2 6.3C4.1 7.7 2.6 9.8 2 12c1 2.5 5 7 10 7 1.9 0 3.6-.6 5-1.5"/><path d="M9.9 9.9a3 3 0 0 0 4.2 4.2"/></svg>';
export const EYE = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2 12c1-2.5 5-7 10-7s9 4.5 10 7c-1 2.5-5 7-10 7S3 14.5 2 12z"/><circle cx="12" cy="12" r="3"/></svg>';

function icon(svg) {
  const s = document.createElement('span');
  s.className = 'eye';
  s.innerHTML = svg;
  return s;
}

function confirmHide(spec) {
  return new Promise((resolve) => {
    const dialog = node('dialog', { class: 'hide-dialog', 'aria-labelledby': 'hide-dialog-title' });
    const done = (ok) => {
      dialog.close();
      dialog.remove();
      resolve(ok);
    };
    const cancel = node('button', { type: 'button', class: 'btn', text: 'Cancelar', onclick: () => done(false) });
    const ok = node('button', { type: 'button', class: 'btn danger', onclick: () => done(true) }, icon(EYE_OFF), `Esconder ${kindName(spec.kind)}`);
    dialog.append(
      node('h2', { id: 'hide-dialog-title', text: `Esconder «${spec.title}»?` }),
      node('p', { text: WHAT_GOES[spec.kind] }),
      node('p', { class: 'keep' }, 'Só ', node('b', { text: 'este navegador' }), ' continua vendo, com a marca de escondido, e só ele pode mostrar de novo. No jogo nada muda.'),
      node('p', { class: 'fine', text: 'Fica guardado num cookie. Se os cookies forem apagados, o site tenta reconhecer este navegador pelo jeito dele (tela, idioma, placa de vídeo) — num navegador ou aparelho diferente, não dá.' }),
      node('div', { class: 'actions' }, cancel, ok),
    );
    dialog.addEventListener('cancel', (e) => {
      e.preventDefault();
      done(false);
    });
    dialog.addEventListener('click', (e) => e.target === dialog && done(false));
    document.body.append(dialog);
    dialog.showModal();
    cancel.focus();
  });
}

let toastTimer = 0;
export function toast(text, action = null) {
  let box = document.getElementById('toast');
  if (!box) {
    box = node('div', { id: 'toast', class: 'toast', role: 'status', 'aria-live': 'polite' });
    document.body.append(box);
  }
  box.replaceChildren(node('span', { text }), action ? node('button', { type: 'button', text: action.text, onclick: () => { box.hidden = true; action.run(); } }) : null);
  box.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (box.hidden = true), action ? 8000 : 5000);
}

// ---------- esconder e mostrar ----------

// spec: { kind, title, x, z, box?, name? }. Resolve o escondido criado, ou null se cancelou/falhou.
export async function hide(spec) {
  if (!(await confirmHide(spec))) return null;
  try {
    const h = await post('api/hide', { hide: spec });
    mine = [...mine, h];
    changed();
    toast(`«${spec.title}» escondido. Só você vê.`, { text: 'Desfazer', run: () => unhide(h.id) });
    return h;
  } catch (err) {
    toast(err.message === 'limite' ? 'Limite de escondidos deste navegador.' : `Não deu para esconder: ${err.message}`);
    return null;
  }
}

export async function unhide(id) {
  const h = myHide(id);
  try {
    await post('api/unhide', { id });
    mine = mine.filter((o) => o.id !== id);
    changed();
    toast(`«${h?.title ?? 'Escondido'}» apareceu de novo para todo mundo.`);
    return true;
  } catch (err) {
    toast(`Não deu para mostrar de novo: ${err.message}`);
    return false;
  }
}

// Linha de acao no pe do cartao: "Esconder" ou, se ja e meu, o aviso com "Mostrar de novo".
export function hideControl(spec, hiddenId) {
  if (hiddenId) {
    const h = myHide(hiddenId);
    // Coisa dentro de uma base escondida: quem volta e a base inteira.
    const inBase = h?.kind === 'base' && spec.kind !== 'base';
    return node('div', { class: 'hidden-note' },
      icon(EYE_OFF),
      node('span', {},
        node('b', { text: inBase ? `Na base escondida «${h.title}»` : 'Escondido' }), ' — só você vê',
        h?.agent ? node('small', { text: ` · escondido no ${h.agent}` }) : null),
      node('button', { type: 'button', class: 'btn small', onclick: () => unhide(hiddenId) }, icon(EYE), inBase ? 'Mostrar a base' : 'Mostrar de novo'));
  }
  return node('button', { type: 'button', class: 'hide-btn', title: `Tirar do site para todo mundo`, onclick: () => hide(spec) },
    icon(EYE_OFF), `Esconder ${kindName(spec.kind)}`);
}
