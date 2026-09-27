// O mapa do jogo num canvas: renderer WebGL, camada 2D por cima, arrastar/zoom/pinca/clique. Usado
// pelo mapa principal e pelas paginas de base e de jogador; quem usa desenha a camada de cima em onDraw.
import { MapRenderer, parseTerrain, sunDirection, WORLD_SIZE } from './mapgl.js';
import { groupSettlements, parsePieces } from './pieces.js';

export const GAME = 'game';
const MIN_MPP = 0.25; // zoom maximo do jogo: 0,015 do mapa na tela
const MAX_MPP = 40;
// Arrasto menor que isto (px) ainda e clique.
const CLICK_SLOP = 6;

// Luz de um dia limpo no prado, em valores de inspetor (sRGB); o renderer lineariza.
const DAY = {
  sunDir: sunDirection(0.42),
  sunColor: [0.72, 0.74, 0.78, 1],
  ambientColor: [0.52, 0.57, 0.7, 1],
  sunFogColor: [0.7, 0.7, 0.72, 1],
};

// Indice do terrain.bin -> cor do Minimap. O prefab sobrescreve as do codigo; terras nebulosas
// e oceano nao sao serializados.
function biomeColors(art) {
  const b = art.biomes;
  const list = [[1, 1, 1], b.meadows, b.swamp, b.mountain, b.blackforest, b.heath, b.ashlands, b.deepnorth, [0.2, 0.2, 0.2]];
  return list.map((c) => c.map((v) => Math.round(v * 255)));
}

export function loadIcon(name) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => resolve([name, img]);
    img.onerror = () => resolve([name, null]);
    img.src = `${GAME}/icons/${name}.png`;
  });
}

function emptyPieces() {
  const b = new ArrayBuffer(8);
  new Uint8Array(b).set([86, 80, 67, 49]);
  return b;
}

const AMBIENT_MS = 1000 / 12;
const AMBIENT_UNFOCUSED_MS = 1000 / 2;

export class MapView {
  constructor({ map, overlay, view, icons = [], onDraw, onHover, onClick, onChange }) {
    this.map = map;
    this.overlay = overlay;
    this.ctx = overlay.getContext('2d');
    this.view = { x: 0, z: 0, metersPerPixel: 3.2, pixelRatio: 1, ...view };
    this.iconNames = icons;
    this.icons = {};
    this.onDraw = onDraw;
    this.onHover = onHover;
    this.onClick = onClick;
    this.onChange = onChange;
    this.renderer = null;
    this.biomeTable = null;
    this.terrain = null;
    this.pieces = null;
    this.settlements = null;
    this.dirty = true;
    this.lastDraw = -Infinity;
    this.flight = null;
    this.resize = this.resize.bind(this);
    this.resize();
    this.bindInput();
    new ResizeObserver(this.resize).observe(map);
    requestAnimationFrame((t) => this.frame(t));
  }

  get width() { return this.map.clientWidth; }
  get height() { return this.map.clientHeight; }

  static clampMpp(v) {
    return Math.min(MAX_MPP, Math.max(MIN_MPP, v));
  }

  resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.view.pixelRatio = dpr;
    for (const c of [this.map, this.overlay]) {
      c.width = Math.round(c.clientWidth * dpr);
      c.height = Math.round(c.clientHeight * dpr);
    }
    this.dirty = true;
  }

  toScreen(x, z) {
    const v = this.view;
    return [this.width / 2 + (x - v.x) / v.metersPerPixel, this.height / 2 - (z - v.z) / v.metersPerPixel];
  }

  toWorld(sx, sy) {
    const v = this.view;
    return [v.x + (sx - this.width / 2) * v.metersPerPixel, v.z - (sy - this.height / 2) * v.metersPerPixel];
  }

  // Coordenadas do evento relativas ao canvas (a pagina pode ter o mapa em qualquer lugar).
  local(e) {
    const r = this.map.getBoundingClientRect();
    return [e.clientX - r.left, e.clientY - r.top];
  }

  zoomAt(sx, sy, factor) {
    const [wx, wz] = this.toWorld(sx, sy);
    this.view.metersPerPixel = MapView.clampMpp(this.view.metersPerPixel * factor);
    const [nx, nz] = this.toWorld(sx, sy);
    this.view.x += wx - nx;
    this.view.z += wz - nz;
    this.changed();
  }

  // Centra em (x, z); com `mpp`, tambem ajusta o zoom.
  goTo(x, z, mpp) {
    this.view.x = x;
    this.view.z = z;
    if (mpp) this.view.metersPerPixel = MapView.clampMpp(mpp);
    this.changed();
  }

  // Enquadra a caixa com folga de `pad` px.
  fit(minX, minZ, maxX, maxZ, pad = 40) {
    const w = Math.max(10, maxX - minX);
    const h = Math.max(10, maxZ - minZ);
    const mpp = Math.max(w / Math.max(50, this.width - pad * 2), h / Math.max(50, this.height - pad * 2));
    this.goTo((minX + maxX) / 2, (minZ + maxZ) / 2, mpp);
  }

  changed() {
    const half = WORLD_SIZE / 2;
    this.view.x = Math.max(-half, Math.min(half, this.view.x));
    this.view.z = Math.max(-half, Math.min(half, this.view.z));
    this.dirty = true;
    this.onChange?.(this.view);
  }

  invalidate() {
    this.dirty = true;
  }

  bindInput() {
    const map = this.map;
    const pointers = new Map();
    let pinch = null;
    let press = null;
    map.addEventListener('pointerdown', (e) => {
      this.flight = null;
      map.setPointerCapture(e.pointerId);
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      map.classList.add('dragging');
      press = pointers.size === 1 ? { x: e.clientX, y: e.clientY, moved: false } : null;
      if (pointers.size === 2) {
        const [a, b] = [...pointers.values()];
        pinch = { dist: Math.hypot(a.x - b.x, a.y - b.y) };
      }
    });
    map.addEventListener('pointermove', (e) => {
      const prev = pointers.get(e.pointerId);
      if (!prev) {
        if (e.pointerType === 'mouse') this.onHover?.(...this.local(e));
        return;
      }
      const cur = { x: e.clientX, y: e.clientY };
      pointers.set(e.pointerId, cur);
      if (press && Math.hypot(cur.x - press.x, cur.y - press.y) > CLICK_SLOP) press.moved = true;
      if (pointers.size === 2 && pinch) {
        const [a, b] = [...pointers.values()];
        const dist = Math.hypot(a.x - b.x, a.y - b.y);
        const r = map.getBoundingClientRect();
        if (dist > 0) this.zoomAt((a.x + b.x) / 2 - r.left, (a.y + b.y) / 2 - r.top, pinch.dist / dist);
        pinch.dist = dist;
        return;
      }
      if (pointers.size === 1 && (!press || press.moved)) {
        this.view.x -= (cur.x - prev.x) * this.view.metersPerPixel;
        this.view.z += (cur.y - prev.y) * this.view.metersPerPixel;
        this.changed();
      }
    });
    const up = (e) => {
      const wasClick = press && !press.moved && pointers.size === 1 && e.type === 'pointerup';
      pointers.delete(e.pointerId);
      if (pointers.size < 2) pinch = null;
      if (!pointers.size) map.classList.remove('dragging');
      if (wasClick) this.onClick?.(...this.local(e), e);
      press = null;
    };
    map.addEventListener('pointerup', up);
    map.addEventListener('pointercancel', up);
    map.addEventListener('pointerleave', () => this.onHover?.(null, null));
    map.addEventListener(
      'wheel',
      (e) => {
        e.preventDefault();
        this.flight = null;
        const delta = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
        this.zoomAt(...this.local(e), Math.exp(delta * 0.0015));
      },
      { passive: false },
    );
    map.addEventListener('dblclick', (e) => this.zoomAt(...this.local(e), 0.5));
  }

  // Agua e nuvens so pedem poucos quadros: a toda velocidade apenas quando a vista muda. Um mapa
  // parado a 60 quadros e uma GPU (ou CPU, sem aceleracao) inteira para um desenho quase fixo.
  frame(now) {
    const ambient = document.hasFocus() ? AMBIENT_MS : AMBIENT_UNFOCUSED_MS;
    if (this.renderer && (this.dirty || now - this.lastDraw >= ambient)) {
      this.lastDraw = now;
      const t = now / 1000;
      this.renderer.draw(this.view, { ...DAY, cloudOffset: [t * 0.0012, 0, t * 0.0007] }, t);
    }
    if (this.dirty) {
      this.dirty = false;
      const ctx = this.ctx;
      ctx.setTransform(this.view.pixelRatio, 0, 0, this.view.pixelRatio, 0, 0);
      ctx.clearRect(0, 0, this.width, this.height);
      this.onDraw?.(ctx, this);
    }
    requestAnimationFrame((t) => this.frame(t));
  }

  set showPieces(on) {
    if (this.renderer) this.renderer.showPieces = on;
    this.dirty = true;
  }

  // Arte, icones e o mundo de agora. Lanca erro se o WebGL2 ou o terreno faltarem.
  async start() {
    const art = await (await fetch(`${GAME}/art.json`)).json();
    const r = new MapRenderer(this.map);
    this.biomeTable = biomeColors(art);
    const [, iconList] = await Promise.all([r.loadArt(GAME, art), Promise.all(this.iconNames.map(loadIcon))]);
    await this.loadWorld(null, r);
    this.icons = Object.fromEntries(iconList);
    await document.fonts.load('700 16px Norse');
    this.renderer = r;
    this.dirty = true;
  }

  // Terreno e construcoes de agora ou de um dia do historico, trocados no renderer ja montado.
  async loadWorld(date, r = this.renderer) {
    const base = date ? `data/days/${date}/` : 'data/';
    const [terrainBuf, piecesBuf] = await Promise.all([
      // Sempre revalida: a Cloudflare manda o navegador guardar .bin por 4 h.
      fetch(base + 'terrain.bin', { cache: 'no-cache' }).then((res) => {
        if (!res.ok) throw new Error(`terreno ${res.status}`);
        return res.arrayBuffer();
      }),
      // Sem construcoes o mapa abre do mesmo jeito.
      fetch(base + 'pieces.bin', { cache: 'no-cache' })
        .then((res) => (res.ok ? res.arrayBuffer() : null))
        .catch(() => null),
    ]);
    this.pieces = piecesBuf ? parsePieces(piecesBuf) : null;
    this.settlements = this.pieces ? groupSettlements(this.pieces) : { groups: [], pieceGroup: new Int32Array(0), cells: new Map(), cell: 6 };
    if (r.pieces) r.setPieces(this.pieces ?? parsePieces(emptyPieces()));
    this.terrain = parseTerrain(terrainBuf);
    r.setTerrain(this.terrain, this.biomeTable);
    this.dirty = true;
  }

  // Recorte do mapa em outro lugar, sem segundo contexto WebGL: desenha a outra vista no mesmo canvas,
  // copia o miolo e redesenha a vista atual antes de o navegador compor o quadro. null sem renderer.
  snapshot(x, z, metersPerPixel, width, height) {
    if (!this.renderer) return null;
    const dpr = this.view.pixelRatio;
    const out = document.createElement('canvas');
    out.width = Math.round(width * dpr);
    out.height = Math.round(height * dpr);
    const t = performance.now() / 1000;
    const env = { ...DAY, cloudOffset: [t * 0.0012, 0, t * 0.0007] };
    this.renderer.draw({ ...this.view, x, z, metersPerPixel }, env, t);
    const sw = Math.min(out.width, this.map.width);
    const sh = Math.min(out.height, this.map.height);
    out.getContext('2d').drawImage(this.map, (this.map.width - sw) / 2, (this.map.height - sh) / 2, sw, sh, 0, 0, out.width, out.height);
    this.renderer.draw(this.view, env, t);
    return out;
  }

  // Leva a vista ate (x, z) num voo curto; `mpp` ajusta o zoom no caminho. Resolve true ao chegar,
  // false se o usuario pegou o mapa no meio.
  flyTo(x, z, mpp = this.view.metersPerPixel, ms = 700) {
    const from = { ...this.view };
    const to = { x, z, metersPerPixel: MapView.clampMpp(mpp) };
    // Longe, sobe um pouco no meio para o destino aparecer chegando.
    const dist = Math.hypot(to.x - from.x, to.z - from.z);
    const top = Math.max(from.metersPerPixel, to.metersPerPixel, Math.min(MAX_MPP, dist / Math.max(1, this.width) * 0.8));
    const t0 = performance.now();
    this.flight = t0;
    let done;
    const arrived = new Promise((resolve) => (done = resolve));
    const step = (now) => {
      if (this.flight !== t0) return done(false);
      const k = Math.min(1, (now - t0) / ms);
      const e = k < 0.5 ? 2 * k * k : 1 - (-2 * k + 2) ** 2 / 2;
      const base = from.metersPerPixel + (to.metersPerPixel - from.metersPerPixel) * e;
      const lift = Math.sin(Math.PI * e) * Math.max(0, top - Math.max(from.metersPerPixel, to.metersPerPixel));
      this.goTo(from.x + (to.x - from.x) * e, from.z + (to.z - from.z) * e, base + lift);
      if (k < 1) requestAnimationFrame(step);
      else done(true);
    };
    requestAnimationFrame(step);
    return arrived;
  }

  // Desenha um icone do jogo centrado em (sx, sy).
  icon(name, sx, sy, size, alpha = 1) {
    const img = this.icons[name];
    if (!img) return;
    const ctx = this.ctx;
    const k = size / Math.max(img.width, img.height);
    ctx.globalAlpha = alpha;
    ctx.drawImage(img, sx - (img.width * k) / 2, sy - (img.height * k) / 2, img.width * k, img.height * k);
    ctx.globalAlpha = 1;
  }

  // Rotulo com a fonte do jogo; devolve false se colidiu com uma caixa de `boxes`.
  label(text, sx, sy, size, boxes = null) {
    if (!text) return false;
    const ctx = this.ctx;
    ctx.font = `700 ${size}px Norse, "Averia Serif Libre", serif`;
    const tw = ctx.measureText(text).width;
    const box = [sx - tw / 2 - 2, sy - 2, sx + tw / 2 + 2, sy + size + 2];
    if (boxes) {
      if (boxes.some((b) => box[0] < b[2] && box[2] > b[0] && box[1] < b[3] && box[3] > b[1])) return false;
      boxes.push(box);
    }
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    ctx.lineJoin = 'round';
    ctx.lineWidth = 3;
    ctx.strokeStyle = 'rgba(0,0,0,0.75)';
    ctx.strokeText(text, sx, sy);
    ctx.fillStyle = '#f4efe6';
    ctx.fillText(text, sx, sy);
    return true;
  }
}
