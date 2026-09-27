// Construcoes no mapa: o pieces.bin do plugin (retangulos vistos de cima) rasterizado numa textura do
// tamanho da tela. O shader do mapa le essa textura e pinta as pecas com a mesma luz, o mesmo
// pergaminho, as mesmas nuvens e a mesma nevoa do terreno, com contorno de tinta e sombra do sol.

// Tipo do pieces.bin (PieceKind do plugin) -> nome e cor de tinta (sRGB).
export const KINDS = [
  { name: 'madeira', color: [0.62, 0.43, 0.26] },
  { name: 'madeira nobre', color: [0.5, 0.31, 0.19] },
  { name: 'madeira de Yggdrasil', color: [0.66, 0.62, 0.5] },
  { name: 'pedra', color: [0.54, 0.53, 0.5] },
  { name: 'mármore', color: [0.36, 0.37, 0.41] },
  { name: 'grausten', color: [0.42, 0.37, 0.35] },
  { name: 'ferro', color: [0.33, 0.33, 0.35] },
  { name: 'antigo', color: [0.4, 0.38, 0.28] },
  { name: 'gelo', color: [0.78, 0.88, 0.93] },
  { name: 'móveis', color: [0.72, 0.58, 0.4] },
  { name: 'plantação', color: [0.47, 0.6, 0.24] },
  { name: 'barco', color: [0.44, 0.29, 0.18] },
];
export const CROP = 10;
export const SHIP = 11;
const STRIDE = 29;
// Chao limpo e pisado em volta de cada peca, em metros.
const GROUND_GROW_M = 3;

// 'VPC1' | u32 n | n x (f32 x, f32 z, f32 y, f32 cos, f32 sin, f32 meiaX, f32 meiaZ, u8 tipo)
export function parsePieces(buffer) {
  const dv = new DataView(buffer);
  if (String.fromCharCode(...new Uint8Array(buffer, 0, 4)) !== 'VPC1') throw new Error('construções em formato desconhecido');
  const n = dv.getUint32(4, true);
  const f = new Float32Array(n * 7);
  const kind = new Uint8Array(n);
  for (let i = 0; i < n; i++) {
    const o = 8 + i * STRIDE;
    for (let k = 0; k < 7; k++) f[i * 7 + k] = dv.getFloat32(o + k * 4, true);
    kind[i] = dv.getUint8(o + 28);
  }
  return { n, f, kind };
}

const VERT = `#version 300 es
in vec2 aCorner;
in vec4 iPos;   // x, z, y, tipo
in vec4 iRot;   // cos, sin, meiaX, meiaZ
uniform vec2 uCenter, uHalf;   // metros de mundo
uniform float uMinHalf;         // meia largura minima: peca longe nao some
uniform float uGrow;            // passada do chao pisado: quanto a peca cresce em volta
out vec2 vLocal;
out vec2 vHalf;
flat out float vKind;
flat out float vY;
flat out float vTall;
flat out float vSeed;
flat out float vSmall;
flat out float vGrow;
void main() {
  vec2 size = iRot.zw;
  float kind = iPos.w;
  // Plantacao ocupa o canteiro em volta; o que for fino e comprido e parede ou poste.
  if (kind == 10.0) size *= 1.35;
  vTall = kind < 9.0 && min(size.x, size.y) < 0.3 && max(size.x, size.y) > 0.3 ? 1.0 : 0.0;
  // Plantacao espalhada nao abre clareira: so o canteiro.
  vGrow = kind == 10.0 ? min(uGrow, 0.8) : uGrow;
  vec2 drawn = max(size + vGrow, vec2(uMinHalf));
  // Menor que ~2 pixels: vira mancha de cor, sem contorno nem forma.
  vSmall = max(size.x, size.y) < uMinHalf * 1.4 ? 1.0 : 0.0;
  vec2 l = aCorner * drawn;
  // Giro do Unity em torno de Y: (x, z) -> (x cos + z sin, -x sin + z cos).
  vec2 w = iPos.xy + vec2(l.x * iRot.x + l.y * iRot.y, -l.x * iRot.y + l.y * iRot.x);
  vLocal = l;
  vHalf = drawn;
  vKind = kind;
  vY = iPos.z;
  vSeed = fract(sin(dot(iPos.xy, vec2(12.9898, 78.233))) * 43758.5453);
  // O mais alto na frente; parede ganha do piso na mesma altura, plantacao perde de tudo.
  float bias = vTall > 0.5 ? -0.00002 : kind == 10.0 ? 0.00002 : 0.0;
  gl_Position = vec4((w - uCenter) / uHalf, clamp(0.5 - vY / 4000.0 + bias, 0.0, 1.0), 1.0);
}`;

const FRAG = `#version 300 es
precision highp float;
in vec2 vLocal;
in vec2 vHalf;
flat in float vKind;
flat in float vY;
flat in float vTall;
flat in float vSeed;
flat in float vSmall;
flat in float vGrow;
uniform vec3 uColors[12];
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGround;
void main() {
  if (vGrow > 0.0) {
    // Chao pisado de cantos redondos.
    vec2 e = max(abs(vLocal) - (vHalf - vGrow), 0.0);
    if (length(e) > vGrow) discard;
    outGround = vec4(1.0);
    return;
  }
  vec3 c = uColors[int(vKind)];
  vec2 q = vLocal / vHalf;
  if (vSmall > 0.5) {
  } else if (vKind == 10.0) {
    // Canteiro de terra lavrada com a planta no meio.
    c = length(q) < 0.62 ? c : vec3(0.3, 0.22, 0.14);
  } else if (vKind == 11.0) {
    // Casco: pontudo no eixo comprido.
    vec2 a = vHalf.x > vHalf.y ? q : q.yx;
    if (abs(a.y) > 1.0 - a.x * a.x) discard;
  }
  c *= 0.9 + 0.2 * vSeed;
  outGround = vec4(1.0);
  // Altura e marcas no alfa (ver mapgl.js): 0 = sem peca.
  outColor = vec4(c, 1000.0 + vY + vTall * 10000.0 + vSmall * 20000.0);
}`;

function compile(gl, type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src);
  gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
  return s;
}

const srgbToLinear = (c) => (c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));

export class PiecesLayer {
  // Precisa de alvo float (altura da peca com precisao de centimetro); sem ele, nada de construcoes.
  static supported(gl) {
    return !!gl.getExtension('EXT_color_buffer_float');
  }

  // unit: unidade de textura reservada para a textura das pecas no shader do mapa.
  constructor(gl, unit) {
    this.gl = gl;
    this.unit = unit;
    const prog = gl.createProgram();
    gl.attachShader(prog, compile(gl, gl.VERTEX_SHADER, VERT));
    gl.attachShader(prog, compile(gl, gl.FRAGMENT_SHADER, FRAG));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
    this.prog = prog;
    this.loc = {
      uCenter: gl.getUniformLocation(prog, 'uCenter'),
      uHalf: gl.getUniformLocation(prog, 'uHalf'),
      uMinHalf: gl.getUniformLocation(prog, 'uMinHalf'),
      uColors: gl.getUniformLocation(prog, 'uColors'),
      uGrow: gl.getUniformLocation(prog, 'uGrow'),
    };
    this.colors = new Float32Array(KINDS.flatMap((k) => k.color.map(srgbToLinear)));
    this.vao = gl.createVertexArray();
    gl.bindVertexArray(this.vao);
    const corner = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, corner);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const aCorner = gl.getAttribLocation(prog, 'aCorner');
    gl.enableVertexAttribArray(aCorner);
    gl.vertexAttribPointer(aCorner, 2, gl.FLOAT, false, 0, 0);
    this.instances = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.instances);
    for (const [name, offset] of [['iPos', 0], ['iRot', 16]]) {
      const l = gl.getAttribLocation(prog, name);
      gl.enableVertexAttribArray(l);
      gl.vertexAttribPointer(l, 4, gl.FLOAT, false, 32, offset);
      gl.vertexAttribDivisor(l, 1);
    }
    gl.bindVertexArray(null);
    this.count = 0;
    this.fbo = gl.createFramebuffer();
    this.color = gl.createTexture();
    this.ground = gl.createTexture();
    this.depth = gl.createRenderbuffer();
    this.size = [0, 0];
    this.resize(1, 1);
  }

  setPieces({ n, f, kind }) {
    const data = new Float32Array(n * 8);
    for (let i = 0; i < n; i++) {
      const s = i * 7;
      const d = i * 8;
      data[d] = f[s]; // x
      data[d + 1] = f[s + 1]; // z
      data[d + 2] = f[s + 2]; // y
      data[d + 3] = kind[i];
      data[d + 4] = f[s + 3]; // cos
      data[d + 5] = f[s + 4]; // sin
      data[d + 6] = f[s + 5]; // meiaX
      data[d + 7] = f[s + 6]; // meiaZ
    }
    const gl = this.gl;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.instances);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
    this.count = n;
  }

  resize(w, h) {
    if (this.size[0] === w && this.size[1] === h) return;
    const gl = this.gl;
    this.size = [w, h];
    gl.activeTexture(gl.TEXTURE0 + this.unit);
    gl.bindTexture(gl.TEXTURE_2D, this.color);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, w, h, 0, gl.RGBA, gl.FLOAT, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.activeTexture(gl.TEXTURE0 + this.unit + 1);
    gl.bindTexture(gl.TEXTURE_2D, this.ground);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.bindRenderbuffer(gl.RENDERBUFFER, this.depth);
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, w, h);
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.color, 0);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT1, gl.TEXTURE_2D, this.ground, 0);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, this.depth);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    this.key = null;
  }

  // Rasteriza as pecas na textura; so refaz quando a vista muda.
  render(view, w, h, enabled) {
    const key = `${view.x},${view.z},${view.metersPerPixel},${w},${h},${enabled},${this.count}`;
    if (key === this.key) return;
    this.key = key;
    const gl = this.gl;
    this.resize(w, h);
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo);
    gl.viewport(0, 0, w, h);
    gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);
    gl.clearBufferfv(gl.COLOR, 0, [0, 0, 0, 0]);
    gl.clearBufferfv(gl.COLOR, 1, [0, 0, 0, 0]);
    gl.clearBufferfv(gl.DEPTH, 0, [1]);
    if (enabled && this.count) {
      const mppDevice = view.metersPerPixel / view.pixelRatio;
      gl.useProgram(this.prog);
      gl.bindVertexArray(this.vao);
      gl.enable(gl.DEPTH_TEST);
      gl.depthFunc(gl.LESS);
      gl.uniform2f(this.loc.uCenter, view.x, view.z);
      gl.uniform2f(this.loc.uHalf, (w * mppDevice) / 2, (h * mppDevice) / 2);
      gl.uniform1f(this.loc.uMinHalf, mppDevice * 0.75);
      gl.uniform3fv(this.loc.uColors, this.colors);
      // Chao pisado: a peca crescida, so no segundo alvo e sem profundidade.
      gl.disable(gl.DEPTH_TEST);
      gl.drawBuffers([gl.NONE, gl.COLOR_ATTACHMENT1]);
      gl.uniform1f(this.loc.uGrow, GROUND_GROW_M);
      gl.uniform1f(this.loc.uMinHalf, mppDevice * 2.0);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, this.count);
      gl.enable(gl.DEPTH_TEST);
      gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.NONE]);
      gl.uniform1f(this.loc.uGrow, 0);
      gl.uniform1f(this.loc.uMinHalf, mppDevice * 0.75);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, this.count);
      gl.disable(gl.DEPTH_TEST);
      gl.bindVertexArray(null);
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  }
}

// Bases: pecas vizinhas (ate ~12 m) agrupadas, para o detalhe ao passar o mouse.
export function groupSettlements({ n, f, kind }, cell = 6) {
  const cells = new Map();
  const keyOf = (cx, cz) => `${cx},${cz}`;
  for (let i = 0; i < n; i++) {
    const cx = Math.floor(f[i * 7] / cell);
    const cz = Math.floor(f[i * 7 + 1] / cell);
    const k = keyOf(cx, cz);
    let c = cells.get(k);
    if (!c) cells.set(k, (c = { cx, cz, items: [], group: -1 }));
    c.items.push(i);
  }
  const groups = [];
  for (const start of cells.values()) {
    if (start.group !== -1) continue;
    const g = { id: groups.length, count: 0, kinds: new Array(KINDS.length).fill(0), minX: Infinity, maxX: -Infinity, minZ: Infinity, maxZ: -Infinity };
    groups.push(g);
    start.group = g.id;
    const stack = [start];
    while (stack.length) {
      const c = stack.pop();
      for (const i of c.items) {
        g.count++;
        g.kinds[kind[i]]++;
        const x = f[i * 7];
        const z = f[i * 7 + 1];
        g.minX = Math.min(g.minX, x);
        g.maxX = Math.max(g.maxX, x);
        g.minZ = Math.min(g.minZ, z);
        g.maxZ = Math.max(g.maxZ, z);
      }
      for (let dx = -2; dx <= 2; dx++) {
        for (let dz = -2; dz <= 2; dz++) {
          const nb = cells.get(keyOf(c.cx + dx, c.cz + dz));
          if (nb && nb.group === -1) {
            nb.group = g.id;
            stack.push(nb);
          }
        }
      }
    }
  }
  const pieceGroup = new Int32Array(n);
  for (const c of cells.values()) for (const i of c.items) pieceGroup[i] = c.group;
  return { groups, pieceGroup, cells, cell };
}

// Peca mais alta sob o ponto (x, z), ou -1.
export function pieceAt({ f }, index, x, z, slack = 0) {
  const { cells, cell } = index;
  let best = -1;
  let bestY = -Infinity;
  const cx = Math.floor(x / cell);
  const cz = Math.floor(z / cell);
  const reach = Math.ceil((slack + 4) / cell);
  for (let dx = -reach; dx <= reach; dx++) {
    for (let dz = -reach; dz <= reach; dz++) {
      const c = cells.get(`${cx + dx},${cz + dz}`);
      if (!c) continue;
      for (const i of c.items) {
        const s = i * 7;
        const rx = x - f[s];
        const rz = z - f[s + 1];
        // Volta ao eixo local: inverso do giro do Unity.
        const lx = rx * f[s + 3] - rz * f[s + 4];
        const lz = rx * f[s + 4] + rz * f[s + 3];
        if (Math.abs(lx) <= f[s + 5] + slack && Math.abs(lz) <= f[s + 6] + slack && f[s + 2] > bestY) {
          best = i;
          bestY = f[s + 2];
        }
      }
    }
  }
  return best;
}
