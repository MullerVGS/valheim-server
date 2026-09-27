// Mapa no estilo do jogo: porte do shader Custom/mapshader do Valheim (material `minimap`) para
// WebGL2. Mesmas texturas de arte, mesmas entradas (bioma, mascara de floresta, altura, nevoa),
// mesma ordem de mistura. O jogo roda em espaco de cor linear: textura sRGB e decodificada na
// amostragem, cor de material entra linearizada e a saida volta para sRGB no fim.

export const WORLD_SIZE = 2048 * 12; // metros cobertos pelo mapa (uv 0..1)

const VERT = `#version 300 es
in vec2 aPos;
uniform vec2 uCenter;   // uv do centro da tela
uniform vec2 uHalf;     // meia tela em uv
out vec2 vUv;
void main() {
  vUv = uCenter + aPos * uHalf;
  gl_Position = vec4(aPos, 0.0, 1.0);
}`;

const FRAG = `#version 300 es
precision highp float;
in vec2 vUv;
out vec4 outColor;

uniform sampler2D uBackground, uFogLayer, uMain, uMask, uHeight, uFog, uWater, uLava, uMountain, uCloud, uForest, uSpace;
uniform vec4 uTime;
uniform vec4 uForestColor, uWaterColor, uWaterColorDeep, uWaterAsh, uWaterAshDeep;
uniform float uZoom, uNormalWidth, uNormalIntensity, uSharedFade;
uniform vec3 uMapCenter, uLightColor, uAmbientLightColor, uCloudOffset, uLava1, uLava2, uSunDir;
uniform vec4 uSunFogColor, uSunColor, uAmbientColor;
uniform float uPixelRatio;

// Amostra com mipmap usando a derivada da uv continua: a uv quantizada tem derivada zero dentro do
// bloco e salto na borda, o que escolheria o mip errado e riscaria a borda de cada bloco.
vec4 tiled(sampler2D t, vec2 q, float s) { return textureGrad(t, q * s, dFdx(vUv) * s, dFdy(vUv) * s); }

float smooth01(float x) { return x * x * (3.0 - 2.0 * x); }
float overlay(float a, float b) { return a <= 0.5 ? 2.0 * a * b : 1.0 - 2.0 * (1.0 - a) * (1.0 - b); }
vec3 toSrgb(vec3 c) {
  c = max(c, 0.0);
  return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

void main() {
  // _zoom * _pixelSize * 35 com _pixelSize = 200 / _zoom: grade fixa de 7000 blocos no mapa (3,5 m).
  const float Q = 7000.0;
  vec2 p = trunc(vUv * Q + 0.5) / Q;

  vec4 bg = tiled(uBackground, p, 5.0);
  vec3 mask = texture(uMask, p).xyz;
  float h = texture(uHeight, p).x;
  vec4 tw = uTime.xxxx * vec4(20.0, 19.0824604, 16.86, 18.8824615);

  // Nevoa com borda ondulando.
  vec2 a = vec2(p.y * 1700.0, p.x * 1200.0) + tw.xy;
  float fogOwn = texture(uFog, p + vec2(sin(a.x), cos(a.y)) * 0.0004).x;
  vec2 b = vec2(p.y * 1846.0, p.x * 1246.8) - tw.zw;
  vec2 fogShared = texture(uFog, p + vec2(cos(b.x), sin(b.y)) * 0.0004).xy;

  // Relevo: normal pela diferenca de altura a oeste e ao sul.
  float hW = texture(uHeight, p - vec2(uNormalWidth, 0.0)).x;
  float hS = texture(uHeight, p - vec2(0.0, uNormalWidth)).x;
  vec3 n = h >= 29.5 ? normalize(vec3(hW - h, uNormalIntensity, hS - h)) : vec3(0.0, 1.0, 0.0);
  float ndl = max(dot(n, normalize(uSunDir)), 0.0);
  vec3 light = ndl * uSunColor.rgb * uLightColor + uAmbientColor.rgb * uAmbientLightColor;

  vec4 col;
  if (h < 29.5) {
    float t = clamp((h - 9.5) * 0.05, 0.0, 1.0);
    float deep = clamp((29.0 - h) / 14.0, 0.0, 1.0);
    vec4 ash = mix(uWaterAshDeep, uWaterAsh, t);
    vec4 water = mix(uWaterColorDeep, uWaterColor, t);
    float ag = smooth01(clamp(mask.z * 20.0, 0.0, 1.0));
    vec4 base = bg * mix(water, ash, ag);
    vec2 wuv = p * 80.0 + vec2(uTime.x * 0.1, sin(p.x * p.y * 4000.0 + tw.x) * 0.01);
    vec4 waves = textureGrad(uWater, wuv, dFdx(vUv) * 80.0, dFdy(vUv) * 80.0);
    col = mix(base, waves, (1.0 - deep) * waves.a);
  } else {
    vec4 paper = tiled(uBackground, p, 40.0);
    float hi = max(max(paper.r, paper.g), paper.b);
    float lo = min(min(paper.r, paper.g), paper.b);
    vec3 value = hi - lo >= 1e-4 ? vec3(hi) : paper.rgb;
    vec4 land = texture(uMain, p) * vec4(value, paper.a);
    col = land * 1.5;

    float lavaMask = tiled(uLava, p, 40.0).x;
    vec2 c = p - 0.5;
    vec2 ang = uTime.yy * vec2(0.0005, -0.00087);
    vec2 r1 = vec2(cos(ang.x) * c.x + sin(ang.x) * c.y, cos(ang.x) * c.y - sin(ang.x) * c.x) + 0.5;
    vec2 r2 = vec2(cos(ang.y) * c.x + sin(ang.y) * c.y, cos(ang.y) * c.y - sin(ang.y) * c.x) + 0.5;
    float la = tiled(uLava, r1, 80.0).x;
    float lb = tiled(uLava, r2, 60.0).x;
    float swirl = pow(overlay(lavaMask, overlay(la, lb)), 2.5);
    vec3 lava = mix(uLava1, uLava2, swirl);
    float lavaT = clamp((h - 30.5) * 0.2, 0.0, 1.0) * mask.z;
    col.rgb = mix(col.rgb, lava, overlay(lavaT, lavaMask));
  }

  // Rocha acima de 70 m, neve no plano acima de 80 m.
  vec4 rock = tiled(uMountain, p, 70.0);
  col = mix(col, rock, clamp((h - 70.0) * 0.04, 0.0, 1.0) * rock.a);
  float flatness = clamp((n.y - 0.14) * 6.25, 0.0, 1.0);
  float lum = clamp(dot(col.rgb, vec3(1.0)) * 1.5, 0.0, 1.0);
  float snow = max(clamp((h - 80.0) * 0.05, 0.0, 1.0) * flatness - lum, 0.0);
  col.rgb = mix(col.rgb, vec3(0.5), snow) * light;

  // Linha escura da costa, mais grossa com o mapa afastado.
  float coastWidth = clamp(uZoom * 50.0, 2.0, 10.0);
  col = mix(col, vec4(0.02, 0.01, 0.01, 1.0), 1.0 - min(abs(h - 29.5) / coastWidth, 1.0));

  // Bruma das terras nebulosas.
  vec4 ca = uTime.xxxx * vec4(5.0, 4.7706151, 5.0, 3.2706151) + vec4(p.y * 850.0, p.x * 600.0, p.y * 750.0, p.x * 300.0);
  vec4 wob = vec4(sin(ca.x), cos(ca.y), sin(ca.z), cos(ca.w)) * 0.01;
  float c1 = textureGrad(uCloud, p * 15.0 + wob.xy, dFdx(vUv) * 15.0, dFdy(vUv) * 15.0).a;
  float c2 = textureGrad(uCloud, p * 20.0 + wob.zw, dFdx(vUv) * 20.0, dFdy(vUv) * 20.0).a;
  vec3 sky = uSunColor.rgb + uAmbientColor.rgb;
  col = mix(col, vec4(sky * vec3(c1, c1, 1.0), c1) * vec4(0.7, 0.5, c1, c1), mask.y);
  col = mix(col, vec4(sky * vec3(1.2, 0.7, 0.7), c2), mask.y * c2);

  // Arvores pintadas.
  vec4 trees = tiled(uForest, p, 150.0) * uForestColor;
  trees.rgb = mix(trees.rgb, trees.rgb * light, 0.8);
  col = mix(col, trees, mask.x * trees.a);

  // Nuvens passando.
  float cloud = textureGrad(uCloud, p * 7.0 - uCloudOffset.xz, dFdx(vUv) * 7.0, dFdy(vUv) * 7.0).a;
  col = mix(col, vec4(uLightColor * uSunColor.rgb, 1.0), cloud);

  // Nevoa de guerra: o pergaminho, escurecendo para a borda do mundo.
  float f1 = smooth01(clamp(fogOwn * 2.0, 0.0, 1.0));
  vec2 f2 = clamp(fogShared * 2.0, 0.0, 1.0);
  f2 = f2 * f2 * (3.0 - 2.0 * f2);
  float fog = mix(f1, min(f2.x, f2.y) * 0.5 + f1 * 0.5, uSharedFade);
  vec4 fogColor = fog * bg * (uSunColor + uAmbientColor) * uSunFogColor;
  float d = length(p - 0.5);
  fogColor *= 1.0 - 0.8 * smooth01(min(d * 2.3255813, 1.0));
  col = mix(col, fogColor, clamp(fog, 0.0, 1.0));

  // Fora do mundo, o espaco.
  vec2 suv = gl_FragCoord.xy / uPixelRatio * 0.0007 - uMapCenter.xz * 1e-5;
  vec4 space = texture(uSpace, suv);
  col = mix(col, space, smooth01(clamp((d - 0.42) * 99.99979, 0.0, 1.0)));

  outColor = vec4(toSrgb(col.rgb), 1.0);
}`;

const srgbToLinear = (c) => (c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
// Unity lineariza cor de material e cor global quando o projeto e linear (vetor nao).
const linear = (rgba) => rgba.map((v, i) => (i < 3 ? srgbToLinear(v) : v));

function compile(gl, type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src);
  gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
  return s;
}

function loadImage(url) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error(`falha ao carregar ${url}`));
    img.src = url;
  });
}

// Direcao do sol como o EnvMan calcula: Euler(-90 + angulo, 0, 0) * Euler(0, -90, 0) *
// Euler(-90 + 360 * fracao do dia, 0, 0), e o shader recebe -forward.
export function sunDirection(dayFraction, sunAngle = 45) {
  const rx = (deg, [x, y, z]) => {
    const r = (deg * Math.PI) / 180;
    return [x, y * Math.cos(r) - z * Math.sin(r), y * Math.sin(r) + z * Math.cos(r)];
  };
  const ry = (deg, [x, y, z]) => {
    const r = (deg * Math.PI) / 180;
    return [x * Math.cos(r) + z * Math.sin(r), y, -x * Math.sin(r) + z * Math.cos(r)];
  };
  const f = rx(-90 + sunAngle, ry(-90, rx(-90 + 360 * dayFraction, [0, 0, 1])));
  return [-f[0], -f[1], -f[2]];
}

export class MapRenderer {
  constructor(canvas) {
    const gl = canvas.getContext('webgl2', { antialias: false, alpha: false, preserveDrawingBuffer: false });
    if (!gl) throw new Error('WebGL2 indisponivel');
    this.gl = gl;
    this.canvas = canvas;
    const prog = gl.createProgram();
    gl.attachShader(prog, compile(gl, gl.VERTEX_SHADER, VERT));
    gl.attachShader(prog, compile(gl, gl.FRAGMENT_SHADER, FRAG));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
    this.prog = prog;
    this.loc = {};
    const n = gl.getProgramParameter(prog, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) {
      const name = gl.getActiveUniform(prog, i).name;
      this.loc[name] = gl.getUniformLocation(prog, name);
    }
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    this.vao = gl.createVertexArray();
    gl.bindVertexArray(this.vao);
    const aPos = gl.getAttribLocation(prog, 'aPos');
    gl.enableVertexAttribArray(aPos);
    gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0);
    this.textures = {};
    this.units = {};
  }

  texture(name, setup) {
    const gl = this.gl;
    if (!(name in this.units)) this.units[name] = Object.keys(this.units).length;
    gl.activeTexture(gl.TEXTURE0 + this.units[name]);
    const tex = this.textures[name] ?? gl.createTexture();
    this.textures[name] = tex;
    gl.bindTexture(gl.TEXTURE_2D, tex);
    setup(gl);
  }

  // Arte do jogo: repete, bilinear, sRGB decodificado quando a textura e de cor.
  async loadArt(base, art) {
    const gl = this.gl;
    const slots = {
      _BackgroundTex: 'uBackground',
      _FogLayerTex: 'uFogLayer',
      _WaterTex: 'uWater',
      _lavaTex: 'uLava',
      _MountainTex: 'uMountain',
      _CloudTex: 'uCloud',
      _ForestTex: 'uForest',
      _SpaceTex: 'uSpace',
    };
    await Promise.all(
      Object.entries(slots).map(async ([prop, uniform]) => {
        const info = art.textures[prop];
        const img = await loadImage(`${base}/${info.file}`);
        this.texture(uniform, (gl) => {
          gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
          gl.texImage2D(gl.TEXTURE_2D, 0, info.srgb ? gl.SRGB8_ALPHA8 : gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, img);
          gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
          if (info.mips) {
            gl.generateMipmap(gl.TEXTURE_2D);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, info.filter >= 2 ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR_MIPMAP_NEAREST);
          } else {
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
          }
        });
      }),
    );
    const c = art.colors;
    this.material = {
      uForestColor: linear(c._ForestColor),
      uWaterColor: linear(c._WaterColor),
      uWaterColorDeep: linear(c._WaterColorDeep),
      uWaterAsh: linear(c._WaterColorAshlands),
      uWaterAshDeep: linear(c._WaterColorAshlandsDeep),
      uLightColor: linear(c._lightColor).slice(0, 3),
      uAmbientLightColor: linear(c._ambientLightColor).slice(0, 3),
      uLava1: linear(c._lavaColor1).slice(0, 3),
      uLava2: linear(c._lavaColor2).slice(0, 3),
      uNormalWidth: art.floats._normalWidth,
      uNormalIntensity: art.floats._normalIntensity,
    };
  }

  // Texturas geradas do mundo, como o Minimap: bioma (sRGB), mascara e altura (lineares), nevoa.
  setTerrain(terrain, biomeColors) {
    const { size, height, biome, forest, mist, explored } = terrain;
    const n = size * size;
    const main = new Uint8Array(n * 4);
    const mask = new Uint8Array(n * 4);
    const fog = new Uint8Array(n * 2);
    for (let i = 0; i < n; i++) {
      const c = biomeColors[biome[i]];
      main[i * 4] = c[0];
      main[i * 4 + 1] = c[1];
      main[i * 4 + 2] = c[2];
      main[i * 4 + 3] = 255;
      mask[i * 4] = forest[i];
      mask[i * 4 + 1] = mist[i];
      const f = 255 - explored[i];
      fog[i * 2] = f;
      fog[i * 2 + 1] = f;
    }
    const clamp = (gl) => {
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    };
    this.texture('uMain', (gl) => {
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.SRGB8_ALPHA8, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, main);
      clamp(gl);
    });
    this.texture('uMask', (gl) => {
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, mask);
      clamp(gl);
    });
    this.texture('uHeight', (gl) => {
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.R16F, size, size, 0, gl.RED, gl.HALF_FLOAT, height);
      clamp(gl);
    });
    this.texture('uFog', (gl) => {
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RG8, size, size, 0, gl.RG, gl.UNSIGNED_BYTE, fog);
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
      clamp(gl);
    });
  }

  // view: centro em metros de mundo e metros por pixel CSS. env: luz do momento.
  draw(view, env, seconds) {
    const gl = this.gl;
    const dpr = view.pixelRatio;
    const w = this.canvas.width;
    const h = this.canvas.height;
    gl.viewport(0, 0, w, h);
    gl.useProgram(this.prog);
    gl.bindVertexArray(this.vao);
    for (const [name, unit] of Object.entries(this.units)) {
      if (this.loc[name]) gl.uniform1i(this.loc[name], unit);
    }
    const set = (name, v) => {
      const l = this.loc[name];
      if (!l) return;
      if (typeof v === 'number') gl.uniform1f(l, v);
      else if (v.length === 2) gl.uniform2fv(l, v);
      else if (v.length === 3) gl.uniform3fv(l, v);
      else gl.uniform4fv(l, v);
    };
    const halfW = (w / dpr) * view.metersPerPixel * 0.5;
    const halfH = (h / dpr) * view.metersPerPixel * 0.5;
    set('uCenter', [view.x / WORLD_SIZE + 0.5, view.z / WORLD_SIZE + 0.5]);
    set('uHalf', [halfW / WORLD_SIZE, halfH / WORLD_SIZE]);
    set('uPixelRatio', dpr);
    set('uZoom', Math.min(1, (2 * halfW) / WORLD_SIZE));
    set('uMapCenter', [view.x, 0, view.z]);
    set('uTime', [seconds / 20, seconds, seconds * 2, seconds * 3]);
    set('uSharedFade', 0);
    for (const [k, v] of Object.entries(this.material)) set(k, v);
    set('uSunDir', env.sunDir);
    set('uSunColor', linear(env.sunColor));
    set('uAmbientColor', linear(env.ambientColor));
    set('uSunFogColor', linear(env.sunFogColor));
    set('uCloudOffset', env.cloudOffset);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }
}

// Le data/terrain.bin (ver tools/build_terrain.py).
export function parseTerrain(buffer) {
  const dv = new DataView(buffer);
  const magic = String.fromCharCode(...new Uint8Array(buffer, 0, 4));
  if (magic !== 'VHM1') throw new Error('terreno em formato desconhecido');
  const size = dv.getUint32(4, true);
  const n = size * size;
  let o = 16;
  const height = new Uint16Array(buffer.slice(o, o + n * 2));
  o += n * 2;
  const bytes = () => {
    const a = new Uint8Array(buffer, o, n);
    o += n;
    return a;
  };
  return { size, pixelSize: dv.getFloat32(8, true), height, biome: bytes(), forest: bytes(), mist: bytes(), explored: bytes() };
}
